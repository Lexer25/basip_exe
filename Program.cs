using Basip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

IHost? host = null;

try
{
    host = Host.CreateDefaultBuilder(args).ConfigureServices((context, services) =>
    {
        IConfiguration configuration = context.Configuration;

        // Конфигурация проверяется целиком: в журнал попадают все замечания сразу,
        // а не только первое. Исключение здесь больше не бросается — из-за него
        // приложение падало с "Unhandled exception" ещё до того, как включался
        // файловый журнал, и понять причину было нечем.
        List<string> errors = StartupConfiguration.Validate(configuration);

        if (errors.Count > 0)
        {
            // Вместо Worker запускается служба, которая записывает понятную причину
            // в журнал (консоль, файл, а при работе службой — ещё и в журнал событий
            // Windows) и штатно останавливает приложение с кодом возврата 1.
            services.AddSingleton(new StartupFailure(errors));
            services.AddHostedService<StartupFailureService>();
            return;
        }

        WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>()
            ?? new WorkerOptions();
        services.AddSingleton(options);
        services.AddHostedService<Worker>();
    }).UseWindowsService().ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddConfiguration(context.Configuration.GetSection("Logging"));

        // Уровень журнала для наших категорий (Basip.*) берётся из Log:LogLevel.
        // Без этого правила общий фильтр MEL (минимум по умолчанию — Information) отсекал
        // Debug/Trace, и настройка Log:LogLevel = Trace не давала эффекта: отладочные
        // записи не доходили ни до файла, ни до консоли. Служебные категории (Microsoft.*)
        // при этом остаются на Information и файл не засоряют.
        LogLevel applicationLogLevel = context.Configuration.GetSection("Log").GetValue<LogLevel?>("LogLevel")
            ?? LogLevel.Information;
        logging.AddFilter("Basip", applicationLogLevel);

        // Каталог журнала проверяется пробной записью: если Log:LogFolerPath недоступен
        // (нет прав, каталог не создаётся), файловый провайдер молча ничего не писал и
        // запись об ошибке запуска оставалась только в консоли. В этом случае журнал
        // ведётся в первом доступном каталоге, а причина пишется в сам журнал.
        string configuredLogFolder = context.Configuration.GetSection("Log").GetValue<string>("LogFolerPath") ?? string.Empty;
        LogFileLocation logFile = StartupConfiguration.ResolveLogFile(configuredLogFolder);

        // Версия программы пишется в журнал при каждом запуске. Запись делается напрямую
        // в найденный файл журнала, поэтому она сохраняется и тогда, когда служба
        // не стартует из-за настроек. [sys] означает, что запись не связана с панелью.
        StartupConfiguration.Write(
            LogLevel.Information,
            $"[sys] Версия программы: {ApplicationInfo.Version}. Исполняемый файл: {ApplicationInfo.ExecutablePath}",
            logFile);

        if (!string.IsNullOrWhiteSpace(logFile.Warning))
        {
            StartupConfiguration.Write(LogLevel.Warning, $"[sys] {logFile.Warning}", logFile);
        }

        logging.AddFile(
            // Метка панели выводится сразу после уровня: "[DBG][127] текст".
            // Свойство id_dev подставляет обёртка DeviceScopedLogger (см. ниже),
            // у записей вне обработки панели оно равно "[sys]".
            pathFormat: $"{logFile.Folder}\\ArtonitBasIpTools.log",
            minimumLevel: context.Configuration.GetSection("Log").GetValue<LogLevel?>("LogLevel") ?? LogLevel.Information,
            retainedFileCountLimit: context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit"),
            outputTemplate: $"{{Timestamp:dd-MM-yyyy HH:mm:ss.fff}}\t-\t[{{Level:u3}}]{{{DeviceScopedLogger.DeviceIdPropertyName}}} {{Message}}{{NewLine}}{{Exception}}");

        // Все зарегистрированные провайдеры оборачиваются, чтобы метка id_dev/[sys] была
        // в каждой строке журнала, включая записи хоста (Hosting, Application lifetime),
        // которые создаются не нашим кодом.
        WrapLogProviders(logging);
    }).Build();

    await host.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Ошибка до или во время сборки хоста (например, повреждённый appsettings.json).
    // Хоста и его журнала ещё нет, поэтому запись делается аварийным способом.
    // Версия указывается здесь же: обычная запись с версией до этого места не дошла.
    StartupConfiguration.WriteCritical($"[sys] Запуск приложения прерван (версия программы: {ApplicationInfo.Version}): {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    host?.Dispose();
}

return Environment.ExitCode;

// Оборачивает все зарегистрированные провайдеры журнала: метка панели должна попадать
// и в записи, которые создаёт не наш код (хост, жизненный цикл приложения).
static void WrapLogProviders(ILoggingBuilder logging)
{
    List<ServiceDescriptor> providers = logging.Services
        .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
        .ToList();

    foreach (ServiceDescriptor descriptor in providers)
    {
        logging.Services.Remove(descriptor);
        logging.Services.Add(ServiceDescriptor.Describe(
            typeof(ILoggerProvider),
            services => new DeviceScopedLoggerProvider(CreateLogProvider(descriptor, services)),
            descriptor.Lifetime));
    }
}

static ILoggerProvider CreateLogProvider(ServiceDescriptor descriptor, IServiceProvider services)
{
    if (descriptor.ImplementationInstance is ILoggerProvider instance)
    {
        return instance;
    }

    if (descriptor.ImplementationFactory != null)
    {
        return (ILoggerProvider)descriptor.ImplementationFactory(services);
    }

    if (descriptor.ImplementationType != null)
    {
        return (ILoggerProvider)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType);
    }

    throw new InvalidOperationException($"Не удалось создать провайдер журнала: {descriptor}");
}
