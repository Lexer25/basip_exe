using Microsoft.Extensions.Logging;

namespace Basip
{
    /// <summary>
    /// Контекст обработки панели: id_dev текущей задачи. Значение хранится в AsyncLocal,
    /// поэтому его видят все записи журнала, сделанные во время обработки панели
    /// (Worker, DB, Device), в том числе после await.
    /// </summary>
    public static class DeviceContext
    {
        private static readonly AsyncLocal<int?> currentId = new AsyncLocal<int?>();

        /// <summary>id_dev обрабатываемой панели; null — запись не связана с конкретной панелью.</summary>
        public static int? CurrentId => currentId.Value;

        /// <summary>Устанавливает id_dev на время обработки панели.</summary>
        public static IDisposable Set(int id)
        {
            int? previous = currentId.Value;
            currentId.Value = id;
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly int? previous;

            public Restore(int? previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                currentId.Value = previous;
            }
        }
    }

    /// <summary>
    /// Провайдер-обёртка: все логгеры, созданные фабрикой (включая служебные логгеры хоста
    /// и жизненного цикла), помечаются id_dev, поэтому метка есть в каждой строке журнала.
    /// </summary>
    public sealed class DeviceScopedLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ILoggerProvider inner;

        public DeviceScopedLoggerProvider(ILoggerProvider inner)
        {
            this.inner = inner;
        }

        public ILogger CreateLogger(string categoryName) => new DeviceScopedLogger(inner.CreateLogger(categoryName));

        /// <summary>Области видимости передаются вложенному провайдеру, если он их поддерживает.</summary>
        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            if (inner is ISupportExternalScope support)
            {
                support.SetScopeProvider(scopeProvider);
            }
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Обёртка над ILogger: помечает каждую запись id_dev панели из <see cref="DeviceContext"/>.
    /// Метка выводится сразу после уровня — "[DBG][127] текст" — для этого в состояние записи
    /// добавляется свойство id_dev, которое подставляет шаблон файлового журнала
    /// (см. outputTemplate в Program.cs). Запись, не связанная с обработкой панели,
    /// помечается "[sys]". Метка добавляется ко всем записям, в текстах сообщений
    /// id_dev не дублируется. В консоль метка попадает в самом тексте сообщения.
    /// </summary>
    public sealed class DeviceScopedLogger : ILogger
    {
        /// <summary>Имя свойства, которое подставляет файловый шаблон.</summary>
        public const string DeviceIdPropertyName = "id_dev";

        private const string NoDeviceMark = "[sys]";
        private const string OriginalFormatKey = "{OriginalFormat}";

        private readonly ILogger inner;

        public DeviceScopedLogger(ILogger inner)
        {
            this.inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);

            // Метка панели: [127] для записей об обработке панели, [sys] для остальных.
            int? id = DeviceContext.CurrentId;
            string mark = id.HasValue ? $"[{id.Value}]" : NoDeviceMark;

            // Текст подменяется и в состоянии записи: файловый провайдер (Serilog) формирует
            // сообщение из состояния ("{OriginalFormat}"), а не из переданного formatter,
            // поэтому одной подмены formatter хватает только для консоли.
            IReadOnlyList<KeyValuePair<string, object?>> scopedState = BuildState(state, message, mark);

            inner.Log(logLevel, eventId, scopedState, exception, (_, _) =>
                mark.Length == 0 ? message : $"{mark} {message}");
        }

        /// <summary>
        /// Копия состояния записи: текст сообщения без метки и свойство id_dev с меткой.
        /// Файловый шаблон выводит метку сразу после уровня, а текст — как есть.
        /// </summary>
        private static IReadOnlyList<KeyValuePair<string, object?>> BuildState<TState>(TState state, string message, string mark)
        {
            List<KeyValuePair<string, object?>> items = new List<KeyValuePair<string, object?>>();
            bool formatFound = false;

            if (state is IEnumerable<KeyValuePair<string, object?>> structure)
            {
                foreach (KeyValuePair<string, object?> item in structure)
                {
                    if (item.Key == OriginalFormatKey)
                    {
                        items.Add(new KeyValuePair<string, object?>(OriginalFormatKey, message));
                        formatFound = true;
                    }
                    else if (item.Key != DeviceIdPropertyName)
                    {
                        items.Add(item);
                    }
                }
            }

            if (!formatFound)
            {
                // Свойства есть, а шаблона в состоянии нет — добавляем его первым.
                items.Insert(0, new KeyValuePair<string, object?>(OriginalFormatKey, message));
            }

            items.Add(new KeyValuePair<string, object?>(DeviceIdPropertyName, mark));
            return items;
        }
    }
}
