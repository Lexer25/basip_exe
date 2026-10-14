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

        // Уровень журнала процессов (категория Basip.Processing) задаётся отдельно от
        // Log:LogLevel: подробности обработки панелей и событий можно держать включёнными,
        // не заливая ими основной журнал, где остаются ошибки и сообщения о работе.
        // По умолчанию уровень процессов совпадает с Log:LogLevel.
        LogLevel processLogLevel = context.Configuration.GetSection("Log").GetValue<LogLevel?>("ProcessLogLevel")
            ?? applicationLogLevel;
        logging.AddFilter(LogCategories.Processing, processLogLevel);

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

        // Формат строки у обоих файловых журналов один и тот же: метка панели выводится
        // сразу после уровня ("[DBG][127] текст"). Свойство id_dev подставляет обёртка
        // DeviceScopedLogger (см. ниже), у записей вне обработки панели оно равно "[sys]".
        const string outputTemplate = $"{{Timestamp:dd-MM-yyyy HH:mm:ss.fff}}\t-\t[{{Level:u3}}]{{{DeviceScopedLogger.DeviceIdPropertyName}}} {{Message}}{{NewLine}}{{Exception}}";
        int? retainedFileCount = context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit");

        // Основной журнал: ошибки, проблемы и сообщения о работе — всё, кроме процессов.
        logging.AddFile(
            pathFormat: $"{logFile.Folder}\\ArtonitBasIpTools.log",
            minimumLevel: applicationLogLevel,
            retainedFileCountLimit: retainedFileCount,
            outputTemplate: outputTemplate);
        ServiceDescriptor mainLogProvider = LastLogProvider(logging);

        // Журнал процессов: только категория Basip.Processing — сбор и разбор событий, дампы
        // LogItem. Ведётся в отдельном файле (имя совпадает со службой: basipkeysservices),
        // чтобы отладку не приходилось выискивать среди ошибок.
        logging.AddFile(
            pathFormat: $"{logFile.Folder}\\basipkeysservices.log",
            minimumLevel: processLogLevel,
            retainedFileCountLimit: retainedFileCount,
            outputTemplate: outputTemplate);
        ServiceDescriptor processLogProvider = LastLogProvider(logging);

        // Все зарегистрированные провайдеры оборачиваются, чтобы метка id_dev/[sys] была
        // в каждой строке журнала, включая записи хоста (Hosting, Application lifetime),
        // которые создаются не нашим кодом. Файловые приёмники дополнительно делят записи
        // по категории: процесс — в свой файл, всё остальное — в основной.
        WrapLogProviders(logging, mainLogProvider, processLogProvider);
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

// Последний зарегистрированный приёмник журнала — тот, который только что добавил AddFile
// (он регистрирует ILoggerProvider одиночным дескриптором через AddProvider).
static ServiceDescriptor LastLogProvider(ILoggingBuilder logging)
{
    for (int i = logging.Services.Count - 1; i >= 0; i--)
    {
        if (logging.Services[i].ServiceType == typeof(ILoggerProvider))
        {
            return logging.Services[i];
        }
    }

    throw new InvalidOperationException("Файловый приёмник журнала не зарегистрирован.");
}

// Оборачивает все зарегистрированные провайдеры журнала: метка панели должна попадать
// и в записи, которые создаёт не наш код (хост, жизненный цикл приложения).
static void WrapLogProviders(ILoggingBuilder logging, ServiceDescriptor mainLogProvider, ServiceDescriptor processLogProvider)
{
    List<ServiceDescriptor> providers = logging.Services
        .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
        .ToList();

    foreach (ServiceDescriptor descriptor in providers)
    {
        logging.Services.Remove(descriptor);

        // Маршрут записей задаётся только файловым журналам: консоль и остальные приёмники
        // (например, журнал событий Windows) получают всё, как и раньше.
        Func<string, bool>? route = ReferenceEquals(descriptor, processLogProvider)
            ? LogCategories.IsProcessing
            : ReferenceEquals(descriptor, mainLogProvider)
                ? category => !LogCategories.IsProcessing(category)
                : null;

        logging.Services.Add(ServiceDescriptor.Describe(
            typeof(ILoggerProvider),
            services => WrapLogProvider(descriptor, services, route),
            descriptor.Lifetime));
    }
}

// Каждый приёмник получает метку id_dev/[sys], а файловые — ещё и разделение по категориям.
static ILoggerProvider WrapLogProvider(ServiceDescriptor descriptor, IServiceProvider services, Func<string, bool>? route)
{
    ILoggerProvider provider = CreateLogProvider(descriptor, services);

    if (route != null)
    {
        provider = new CategoryFilterLoggerProvider(provider, route);
    }

    return new DeviceScopedLoggerProvider(provider);
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
