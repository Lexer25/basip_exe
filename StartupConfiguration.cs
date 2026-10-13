using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Basip
{
    /// <summary>
    /// Каталог и файл журнала, в которые пишет приложение.
    /// </summary>
    public sealed class LogFileLocation
    {
        public LogFileLocation(string folder, string filePath, bool isWritable, string? warning)
        {
            Folder = folder;
            FilePath = filePath;
            IsWritable = isWritable;
            Warning = warning;
        }

        /// <summary>Каталог журнала.</summary>
        public string Folder { get; }

        /// <summary>Полный путь к файлу журнала на текущую дату.</summary>
        public string FilePath { get; }

        /// <summary>Признак того, что каталог доступен на запись (проверено пробной записью).</summary>
        public bool IsWritable { get; }

        /// <summary>Предупреждение, если используется не тот каталог, что задан в appsettings.json.</summary>
        public string? Warning { get; }
    }

    /// <summary>
    /// Проверка конфигурации приложения и запись понятных сообщений о причинах,
    /// по которым служба не может быть запущена.
    /// </summary>
    public static class StartupConfiguration
    {
        /// <summary>Каталог журнала по умолчанию (его же использует установщик).</summary>
        public const string DefaultLogFolder = @"C:\ProgramData\Basip\";

        private const string LogFileBaseName = "ArtonitBasIpTools";

        private static LogFileLocation? cachedLogFile;

        /// <summary>Файл конфигурации, который читает приложение (рядом с исполняемым файлом).</summary>
        public static string ConfigurationFilePath =>
            Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        /// <summary>
        /// Имя файла журнала на текущую дату. Файловый провайдер добавляет дату к имени
        /// сам (прокрутка по дням), поэтому записи в него делаются под тем же именем.
        /// </summary>
        public static string LogFileName => $"{LogFileBaseName}-{DateTime.Now:yyyyMMdd}.log";

        /// <summary>
        /// Возвращает список замечаний к конфигурации. Пустой список означает, что запуск
        /// возможен. Исключения не бросаются: замечания записывает в журнал
        /// <see cref="StartupFailureService"/>.
        /// </summary>
        public static List<string> Validate(IConfiguration configuration)
        {
            List<string> errors = new List<string>();

            WorkerOptions? options = configuration.GetSection("Service").Get<WorkerOptions>();
            if (options == null)
            {
                errors.Add($"Секция 'Service' не найдена в файле {ConfigurationFilePath}. Добавьте её и заполните параметр 'Service:db_config'.");
            }
            else if (string.IsNullOrWhiteSpace(options.db_config))
            {
                errors.Add($"Параметр 'Service:db_config' не заполнен. Укажите строку подключения к базе данных Firebird, например: " +
                           "User=SYSDBA;Password=пароль;Database=C:\\Path\\To\\shieldpro_rest.gdb;DataSource=127.0.0.1;Port=3050;");
            }

            string logFolder = configuration.GetSection("Log").GetValue<string>("LogFolerPath") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logFolder))
            {
                errors.Add("Параметр 'Log:LogFolerPath' не заполнен. Укажите каталог для файлового журнала, например: C:\\ProgramData\\Basip\\.");
            }

            return errors;
        }

        /// <summary>
        /// Определяет файл журнала. Сначала берётся каталог из Log:LogFolerPath, но если он
        /// недоступен на запись (нет прав, каталог не создаётся), используется первый
        /// доступный: C:\ProgramData\Basip\, каталог приложения, %LOCALAPPDATA%\Basip.
        /// Без этого файловый провайдер молча ничего не писал и запись об ошибке запуска
        /// оставалась только в консоли.
        /// Результат кэшируется: приложение за время работы использует один и тот же файл.
        /// </summary>
        public static LogFileLocation ResolveLogFile(string? configuredFolder = null)
        {
            if (cachedLogFile != null)
            {
                return cachedLogFile;
            }

            cachedLogFile = ResolveLogFileCore(configuredFolder ?? ReadConfiguredLogFolder());
            return cachedLogFile;
        }

        private static LogFileLocation ResolveLogFileCore(string? configuredFolder)
        {
            string? warning = null;

            if (!string.IsNullOrWhiteSpace(configuredFolder))
            {
                if (TryPrepareFolder(configuredFolder))
                {
                    return new LogFileLocation(configuredFolder, BuildLogFilePath(configuredFolder), true, null);
                }

                warning = $"Каталог журнала '{configuredFolder}' из параметра Log:LogFolerPath недоступен для записи.";
            }

            foreach (string candidate in GetFallbackFolders())
            {
                if (!string.IsNullOrWhiteSpace(configuredFolder) &&
                    string.Equals(candidate.TrimEnd('\\'), configuredFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    continue; // этот каталог уже проверен выше
                }

                if (TryPrepareFolder(candidate))
                {
                    string note = warning == null
                        ? $"Каталог журнала из параметра Log:LogFolerPath не задан, записи ведутся в '{candidate}'."
                        : $"{warning} Записи журнала ведутся в '{candidate}'.";

                    return new LogFileLocation(candidate, BuildLogFilePath(candidate), true, note);
                }
            }

            string lastResort = string.IsNullOrWhiteSpace(configuredFolder) ? DefaultLogFolder : configuredFolder;
            return new LogFileLocation(
                lastResort,
                BuildLogFilePath(lastResort),
                false,
                $"{warning} Ни один каталог журнала не доступен для записи, записи будут только в консоли и журнале событий Windows.".Trim());
        }

        private static IEnumerable<string> GetFallbackFolders()
        {
            yield return DefaultLogFolder;
            yield return AppContext.BaseDirectory;
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Basip");
        }

        /// <summary>Полный путь к файлу журнала на текущую дату в указанном каталоге.</summary>
        public static string BuildLogFilePath(string folder) => Path.Combine(folder, LogFileName);

        /// <summary>
        /// Запись помимо штатного журнала: используется, когда запись обязана быть сохранена
        /// независимо от состояния файлового провайдера (ошибки запуска). Сообщение уходит
        /// в консоль и в файл журнала. Исключения не бросаются.
        /// </summary>
        public static void Write(LogLevel level, string message, LogFileLocation? location = null)
        {
            // Формат совпадает с шаблоном файлового журнала: "[УРОВЕНЬ][метка] текст",
            // поэтому сообщение должно начинаться с метки: "[sys]" — панели нет.
            string line = $"{DateTime.Now:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Abbreviate(level)}]{message}";

            try
            {
                Console.Error.WriteLine(line);
            }
            catch (IOException)
            {
                // Консоль недоступна (служба без перенаправленного вывода) — не страшно.
            }

            try
            {
                LogFileLocation target = location ?? ResolveLogFile();
                Directory.CreateDirectory(target.Folder);
                File.AppendAllText(target.FilePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Файл журнала недоступен: сообщение уже выведено в консоль,
                // падать из-за невозможности записать журнал нельзя.
            }
        }

        /// <summary>Запись об ошибке запуска, когда штатный журнал ещё не работает.</summary>
        public static void WriteCritical(string message) => Write(LogLevel.Critical, message);

        /// <summary>Проверка каталога пробной записью: создаётся каталог и временный файл.</summary>
        private static bool TryPrepareFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);

                string probe = Path.Combine(folder, $".basip-write-test-{Environment.ProcessId}");
                File.AppendAllText(probe, string.Empty);
                File.Delete(probe);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Чтение Log:LogFolerPath прямо из appsettings.json — используется, когда
        /// конфигурация ещё не разобрана хостом. Если файл повреждён или недоступен,
        /// возвращается null: тогда подбирается первый доступный каталог журнала.
        /// </summary>
        private static string? ReadConfiguredLogFolder()
        {
            try
            {
                if (!File.Exists(ConfigurationFilePath))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(ConfigurationFilePath),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("Log", out JsonElement log) &&
                    log.TryGetProperty("LogFolerPath", out JsonElement path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    string? folder = path.GetString();
                    return string.IsNullOrWhiteSpace(folder) ? null : folder;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Файл конфигурации не читается — каталог журнала будет выбран
                // из списка доступных.
            }

            return null;
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Critical => "FTL",
            LogLevel.Error => "ERR",
            LogLevel.Warning => "WRN",
            LogLevel.Information => "INF",
            LogLevel.Debug => "DBG",
            _ => "TRC"
        };
    }

    /// <summary>Причина, по которой служба не может быть запущена.</summary>
    public sealed class StartupFailure
    {
        public StartupFailure(IEnumerable<string> errors)
        {
            Errors = errors.ToList();
        }

        public List<string> Errors { get; }
    }

    /// <summary>
    /// Записывает причину невозможности запуска в журнал (консоль, файловый журнал,
    /// а при работе службой — журнал событий Windows) и штатно останавливает приложение
    /// с кодом возврата 1 вместо падения с необработанным исключением.
    /// </summary>
    public sealed class StartupFailureService : IHostedService
    {
        private readonly StartupFailure failure;
        private readonly IHostApplicationLifetime lifetime;
        private readonly ILogger logger;

        public StartupFailureService(StartupFailure failure, IHostApplicationLifetime lifetime, ILogger<StartupFailureService> logger)
        {
            this.failure = failure;
            this.lifetime = lifetime;

            // Метку [sys] добавляет обёртка провайдеров журнала (см. Program.cs):
            // при ошибке запуска панели нет, поэтому запись помечается как системная.
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Файловый провайдер пишет в каталог, проверенный на запись (см. ResolveLogFile),
            // поэтому все записи ниже попадают и в консоль, и в файл журнала.
            LogFileLocation logFile = StartupConfiguration.ResolveLogFile();

            logger.LogCritical("Служба не запущена: конфигурация приложения заполнена не полностью. Файл конфигурации: {ConfigurationFile}", StartupConfiguration.ConfigurationFilePath);

            foreach (string error in failure.Errors)
            {
                logger.LogCritical("{Error}", error);
            }

            if (logFile.IsWritable)
            {
                logger.LogCritical("Исправьте настройки в файле {ConfigurationFile} и запустите службу снова. Файл журнала: {LogFile}",
                    StartupConfiguration.ConfigurationFilePath, logFile.FilePath);
            }
            else
            {
                logger.LogCritical("{Warning} Исправьте настройки в файле {ConfigurationFile} и запустите службу снова.",
                    logFile.Warning, StartupConfiguration.ConfigurationFilePath);
            }

            Environment.ExitCode = 1;
            lifetime.StopApplication();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
