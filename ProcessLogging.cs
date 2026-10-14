using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basip
{
    /// <summary>
    /// Файловый журнал процессов: basipkeysservices_ГГГГ_ММ_ДД.log.
    /// Serilog-приёмник (logging.AddFile) для этого журнала не подходит: он сам добавляет дату
    /// к имени файла в формате "ГГГГММДД" через дефис и своего формата даты не поддерживает.
    /// Поэтому журнал процессов ведёт этот приёмник: суточная прокрутка, хранение последних
    /// retainedFileCountLimit файлов и тот же формат строки, что у основного журнала —
    /// "ДД-ММ-ГГГГ ЧЧ:мм:сс.fff - [УРОВЕНЬ][метка] сообщение".
    /// Строка сбрасывается на диск сразу после записи: службу могут остановить в любой момент,
    /// и терять последние записи нельзя.
    /// </summary>
    public sealed class ProcessLogFileProvider : ILoggerProvider
    {
        /// <summary>Шаблон имени файла журнала процессов.</summary>
        public const string FileNameTemplate = "basipkeysservices_ГГГГ_ММ_ДД.log";

        private const string FileNamePrefix = "basipkeysservices_";
        private const string FileNameSuffix = ".log";
        private const string DateFormat = "yyyy_MM_dd";
        private const string NoDeviceMark = "[sys]";
        private const string OriginalFormatKey = "{OriginalFormat}";

        /// <summary>Сколько файлов хранить, если в конфигурации значение не задано (столько же у Serilog).</summary>
        private const int DefaultRetainedFileCountLimit = 31;

        /// <summary>Имя файла журнала процессов: по нему же отбираются старые файлы для удаления.</summary>
        private static readonly Regex FileNameRegex = new Regex(
            @"^basipkeysservices_\d{4}_\d{2}_\d{2}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string folder;
        private readonly LogLevel minimumLevel;
        private readonly int retainedFileCountLimit;
        private readonly object sync = new object();

        private DateTime currentDate;
        private StreamWriter? writer;

        public ProcessLogFileProvider(string folder, LogLevel minimumLevel, int? retainedFileCountLimit)
        {
            this.folder = folder;
            this.minimumLevel = minimumLevel;
            this.retainedFileCountLimit = retainedFileCountLimit is > 0
                ? retainedFileCountLimit.Value
                : DefaultRetainedFileCountLimit;
        }

        /// <summary>
        /// Журнал процессов принимает только категорию Basip.Processing (и её подкатегории):
        /// для остальных категорий отдаётся пустой логгер, поэтому разделение журналов
        /// не зависит от того, кто именно пишет запись.
        /// </summary>
        public ILogger CreateLogger(string categoryName) =>
            LogCategories.IsProcessing(categoryName) ? new ProcessLogFileLogger(this) : NullLogger.Instance;

        public void Dispose()
        {
            lock (sync)
            {
                CloseWriter();
            }
        }

        /// <summary>Запись ведётся, если её уровень не ниже уровня журнала процессов (Log:ProcessLogLevel).</summary>
        internal bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

        /// <summary>
        /// Пишет строку журнала. Метка панели и текст без метки берутся из состояния записи: их
        /// подставляет обёртка DeviceScopedLogger (свойство id_dev и "{OriginalFormat}").
        /// </summary>
        internal void Write<TState>(LogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string mark = string.Empty;
            string message = formatter(state, exception);

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                string? deviceMark = null;
                string? format = null;

                foreach (KeyValuePair<string, object?> property in properties)
                {
                    if (property.Key == DeviceScopedLogger.DeviceIdPropertyName)
                    {
                        deviceMark = property.Value?.ToString();
                    }
                    else if (property.Key == OriginalFormatKey && property.Value is string text)
                    {
                        format = text;
                    }
                }

                if (format != null)
                {
                    message = format;
                    mark = deviceMark ?? NoDeviceMark;
                }
            }

            DateTime timestamp = DateTime.Now;

            lock (sync)
            {
                try
                {
                    EnsureWriter(timestamp.Date);

                    writer!.Write(timestamp.ToString("dd-MM-yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    writer.Write("\t-\t[");
                    writer.Write(Abbreviate(logLevel));
                    writer.Write(']');

                    if (mark.Length > 0)
                    {
                        writer.Write(mark);
                        writer.Write(' ');
                    }

                    writer.Write(message);
                    writer.WriteLine();

                    if (exception != null)
                    {
                        writer.WriteLine(exception.ToString());
                    }

                    // Сброс на диск сразу после строки: при аварийной остановке службы
                    // последние записи не должны пропасть.
                    writer.Flush();
                }
                catch (Exception)
                {
                    // Ошибка записи журнала не должна ронять службу: строка просто теряется.
                }
            }
        }

        /// <summary>
        /// Открывает файл текущих суток; при смене даты закрывает предыдущий файл и удаляет лишние.
        /// </summary>
        private void EnsureWriter(DateTime date)
        {
            if (writer != null && date == currentDate)
            {
                return;
            }

            CloseWriter();

            Directory.CreateDirectory(folder);

            string path = Path.Combine(
                folder,
                FileNamePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + FileNameSuffix);

            // Append: служба может перезапуститься в тот же день, и файл суток должен дополняться,
            // а не затираться.
            FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            currentDate = date;

            DeleteOldFiles();
        }

        private void CloseWriter()
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // Закрытие файла журнала не должно мешать остановке службы.
            }

            writer = null;
        }

        /// <summary>
        /// Удаляет самые старые файлы журнала процессов, оставляя retainedFileCountLimit штук.
        /// Дата в имени файла фиксированной длины, поэтому сортировка по имени совпадает
        /// с сортировкой по дате.
        /// </summary>
        private void DeleteOldFiles()
        {
            try
            {
                List<string> files = new List<string>();

                foreach (string file in Directory.EnumerateFiles(folder, FileNamePrefix + "*" + FileNameSuffix))
                {
                    if (FileNameRegex.IsMatch(Path.GetFileName(file)))
                    {
                        files.Add(file);
                    }
                }

                files.Sort(StringComparer.OrdinalIgnoreCase);

                int extra = files.Count - retainedFileCountLimit;
                for (int i = 0; i < extra; i++)
                {
                    File.Delete(files[i]);
                }
            }
            catch (Exception)
            {
                // Недоступный старый файл не должен мешать записи журнала.
            }
        }

        /// <summary>Сокращение уровня как в Serilog (шаблон "{Level:u3}").</summary>
        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "VRB",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "FTL",
            _ => "NON"
        };
    }

    /// <summary>
    /// Логгер журнала процессов. Категорию проверяет провайдер (см.
    /// <see cref="ProcessLogFileProvider.CreateLogger"/>), здесь остаётся только запись.
    /// </summary>
    internal sealed class ProcessLogFileLogger : ILogger
    {
        private readonly ProcessLogFileProvider provider;

        public ProcessLogFileLogger(ProcessLogFileProvider provider)
        {
            this.provider = provider;
        }

        /// <summary>Области видимости в журнале процессов не выводятся.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Write(logLevel, state, exception, formatter);
    }
}
