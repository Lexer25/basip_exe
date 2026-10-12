using Basip;
using Microsoft.Extensions.Logging;

await Host.CreateDefaultBuilder(args).ConfigureServices((context, services) =>
{
    IConfiguration configuration = context.Configuration;

    // Секция отсутствует в appsettings.json или в ней нет db_config —
    // без явной ошибки Worker падал бы позже с NullReferenceException.
    WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>()
        ?? throw new InvalidOperationException(
            "Секция 'Service' не найдена в конфигурации. Добавьте её в appsettings.json.");

    if (string.IsNullOrWhiteSpace(options.db_config))
    {
        throw new InvalidOperationException(
            "Параметр 'Service:db_config' не заполнен. Укажите строку подключения к базе данных Firebird в appsettings.json.");
    }

    services.AddSingleton(options);
    services.AddHostedService<Worker>();
}).ConfigureLogging((context, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
    string path = context.Configuration.GetSection("Log").GetValue<string>("LogFolerPath") ?? string.Empty;
    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException(
            "Параметр 'Log:LogFolerPath' не заполнен. Укажите каталог для файла журнала в appsettings.json.");
    }
    int? retainedFileCountLimit = context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit");
    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
    logging.AddFile(
        pathFormat: $"{path}\\ArtonitBasIpTools.log",
        minimumLevel: context.Configuration.GetSection("Log").GetValue<LogLevel?>("LogLevel") ?? LogLevel.Information,
        retainedFileCountLimit: retainedFileCountLimit,
        outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}");

}).UseWindowsService().Build().RunAsync();