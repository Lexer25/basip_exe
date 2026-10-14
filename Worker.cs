using Microsoft.Extensions.FileSystemGlobbing.Internal.PathSegments;
using Microsoft.Extensions.Logging;
using RestSharp;
using System.Data;
using System.Net;
using System.Text.Json;
using static Basip.WorkerOptions;

namespace Basip
{
    public class Worker : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        private readonly IHostApplicationLifetime lifetime;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;

        private readonly string version;
        private readonly ILogger debugLogger;

        public Worker(ILogger<Worker> logger, WorkerOptions options, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
        {
            // Метку id_dev/[sys] добавляет обёртка провайдеров журнала (см. Program.cs),
            // поэтому здесь используются обычные логгеры.
            this.logger = logger;
            this.options = options;
            this.lifetime = lifetime;

            // Отдельная категория для отладочного вывода Device: запись идёт с уровнем Trace.
            debugLogger = loggerFactory.CreateLogger(Device.DebugLogCategory);

            version = ApplicationInfo.Version;

            // Интервал опроса разбирается так же безопасно, как и timestart:
            // кривое значение в конфиге больше не роняет конструктор службы.
            if (!TryParseTime(options.timeout, out timeout) || timeout <= TimeSpan.Zero)
            {
                logger.LogError($"32 Некорректное значение timeout = \"{options.timeout}\", используется 00:00:10");
                timeout = TimeSpan.FromSeconds(10);
            }

            // Раньше здесь повторно разбирался options.timeout, из-за чего timestart
            // всегда совпадал с интервалом опроса, а значение из конфигурации игнорировалось.
            if (!TryParseTime(options.timestart, out timestart))
            {
                logger.LogError($"35 Некорректное значение timestart = \"{options.timestart}\", используется 00:00:00");
                timestart = TimeSpan.Zero;
            }

            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        // Разбор строки вида "Ч:ММ:СС" без исключения на некорректном значении
        private static bool TryParseTime(string? value, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var parts = value.Split(':');
            if (parts.Length != 3) return false;

            if (!int.TryParse(parts[0], out int hours) ||
                !int.TryParse(parts[1], out int minutes) ||
                !int.TryParse(parts[2], out int seconds))
            {
                return false;
            }

            if (hours < 0 || minutes < 0 || seconds < 0) return false;

            result = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Время первого запуска указывается явно: при run_now = false служба ждёт timestart.
            DateTime firstRun = DateTime.Now.Add(deltasleep);
            logger.LogInformation(@$"32 basip start: {timestart} deltasleep: {deltasleep}, первый запуск: {firstRun:yyyy-MM-dd HH:mm:ss}");
            logger.LogInformation(@$"33 Service basip write and delete card started");

            // Ожидание старта прерывается остановкой службы, иначе Task.Delay
            // задерживал бы штатное завершение на весь deltasleep.
            await Task.Delay(deltasleep, stoppingToken);

            // Выполняем инициализацию БД и получение устройств один раз при старте
            DB db;
            DataRowCollection devices;

            try
            {
                logger.LogInformation($"84 Версия приложения basip: {version}");

                // Создаем экземпляр DB с connection string
                db = new DB(options.db_config ?? string.Empty, logger);

                // Проверяем наличие обязательных таблиц
                if (!db.CheckRequiredTables(logger))
                {
                    logger.LogCritical("92 Служебные таблицы не найдены. Программа завершает работу.");
                    // Штатная остановка хоста вместо Environment.Exit:
                    // Environment.Exit убивал процесс и не давал хосту корректно завершиться.
                    lifetime.StopApplication();
                    return;
                }

                logger.LogInformation("97 Ok connect database");

                // Получаем устройства один раз при старте
                devices = db.GetDevice().Rows;
                logger.LogInformation("71 Зарегистрировано панелей bas-ip: " + devices.Count + " шт.");
            }
            catch (Exception e)
            {
                logger.LogError("104 No connect database: " + options.db_config);
                logger.LogError(e.ToString());
                lifetime.StopApplication();
                return;
            }

            // Основной цикл, который будет выполняться периодически
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation($@"112 Старт итерации");

                try
                {
                    // Запускаем run с уже инициализированными данными
                    run(db);
                }
                catch (Exception ex)
                {
                    logger.LogError("121 Something crash restart everything");
                    logger.LogError(ex.ToString());
                    continue;
                }

                // Время следующего запуска указывается явно: раньше его приходилось
                // считать по строке timeout в уме. Дата нужна на случай перехода через полночь.
                DateTime nextRun = DateTime.Now.Add(timeout);
                logger.LogInformation($@"126 timeout basip: {timeout}, следующий запуск: {nextRun:yyyy-MM-dd HH:mm:ss}");
                await Task.Delay(timeout, stoppingToken);
            }

            logger.LogCritical(@$"49 basip stop");
        }

        private void run(DB db)
        {

            DataRowCollection devices = db.GetDevice().Rows;
            List <Task> tasks = new List<Task>();

            int validDevicesCount = 0;
            logger.LogInformation("70 Start async.");

            foreach (DataRow row in devices)
            {
                try
                {
                    int rowDeviceId = Convert.ToInt32(row["id_dev"]);

                   // if (rowDeviceId != 617) continue;   // ← временно, только для отладки

                    // Записи об этой панели тоже помечаются её id_dev.
                    using IDisposable deviceScope = DeviceContext.Set(rowDeviceId);

                    if (row["IP"] == DBNull.Value || Convert.ToInt32(row["IP"]) == 0)
                    {
                        logger.LogDebug($"149-0 Skipping device ID {rowDeviceId} - no IP address");
                        continue;
                    }
                    logger.LogDebug($"149-1  device ID {rowDeviceId} IP {rowDeviceId} is OK");
                    validDevicesCount++;
                    logger.LogDebug($"149-2 start TaskGet ID {rowDeviceId}");
                    tasks.Add(TaskGet(row, db));
                }
                catch (Exception ex)
                {
                    logger.LogDebug($"149-3 Failed to create device from row ID {row["id_dev"]}: {ex.Message}");
                    continue;
                }
            }

            logger.LogInformation($"164 Processing {validDevicesCount} devices with valid IP addresses");

            if (tasks.Count > 0)
            {
                Task.WaitAll(tasks.ToArray());
            }
            else
            {
                logger.LogInformation("172 No devices with valid IP addresses found");
                return;
            }
        }

        // Изменяем TaskGet, чтобы он принимал DB
        private async Task TaskGet(DataRow row, DB dbForEvents)
        {
            String auth = "Auth OK";
            int currentDeviceId = (int)row["id_dev"];

            // Все записи журнала внутри обработки панели (включая запросы к БД и панели)
            // помечаются её id_dev.
            using IDisposable deviceScope = DeviceContext.Set(currentDeviceId);

            Device dev = new Device(row, options.time_wait_http, debugLogger);

            JsonDocument? deviceInfo = await dev.GetInfo();
            if (deviceInfo == null)
            {
                logger.LogInformation($"192 id_dev={dev.id_dev} IP {dev.base_url} - нет связи. ");
                dbForEvents.FixDeviceErr("Device not connected", currentDeviceId);
                dbForEvents.SetOnlineState(currentDeviceId, 0);
                return;
            }

            // Авторизация выполняется один раз: раньше сразу после этого шёл
            // повторный вызов Auth(), то есть лишний HTTP-запрос на каждую панель за итерацию.
            bool isAuthenticated = await dev.Auth();
            if (!isAuthenticated)
            {
                auth = "Auth ERR";
                logger.LogInformation($"211 id_dev={dev.id_dev} Device {dev.base_url} auth failed");
                dbForEvents.FixDeviceErr("Auth ERR", currentDeviceId);
                return;
            }

            if (!dev.is_online)
            {
                logger.LogDebug($"216 id_dev={dev.id_dev} Device {dev.base_url} offline");
                dbForEvents.FixDeviceErr("Devcice offline", currentDeviceId);
                return;
            }

            // Формирование STRVALUE для BAS_PARAM.ABOUT
            string strValue = BuildAboutString(deviceInfo, auth);
            logger.LogInformation($"202 id_dev={dev.id_dev} Device d_dev={dev.id_dev} {currentDeviceId} ({dev.base_url})  STRVALUE for 'about': {strValue}");
            // Записываем значение в базу данных
            dbForEvents.InsertAbout(currentDeviceId, strValue);
            dbForEvents.SetOnlineState(currentDeviceId, 1);

           // string firmwareVersion = "unknown";
            string _apiVersion = "unknown";

          
             if (deviceInfo.RootElement.TryGetProperty("api_version", out JsonElement fwElement))
               {
                _apiVersion = fwElement.GetString() ?? "unknown";
            }

            int major = 0, middle = 0;
            if (!string.IsNullOrEmpty(_apiVersion) && _apiVersion != "unknown")
            {
                var parts = _apiVersion.Split('.');
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[0], out major);
                    int.TryParse(parts[1], out middle);
                }
            }

            logger.LogInformation($"240 id_dev={dev.id_dev} Device {dev.base_url} apiVersion: {_apiVersion}, major: {major}, middle: {middle}");

            // Мажорная версия API нужна Device, чтобы выбрать адрес журнала
            // ("log/items" для 2.x, "log/list" для 1.x).
            dev.major_api_version = major;

            // Контроль версии API. С версиями 3.0 и выше не работаем.
            // Раньше условие было (major == 3 && middle > 0), поэтому 3.0 проходила как разрешённая.
            if (major >= 3)
            {
                logger.LogWarning($"245 id_dev={dev.id_dev} Device {dev.base_url} apiVersion version {_apiVersion} is higher than allowed (3.x). Stopping work with this panel.");
                return;
            }

            // Используем переданный dbForEvents вместо создания нового
            try
            {
                // major уже разобран выше из того же поля api_version
                int majorVersion = major;
                logger.LogDebug($"288 id_dev={dev.id_dev} major api для обработки событий = {majorVersion}");

                long lastTimestamp = dbForEvents.GetLastEventID(currentDeviceId);
                // Если нет сохраненного timestamp, берем время 1 час назад
                if (lastTimestamp == 0)
                {
                    lastTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
                    logger.LogInformation($"260 id_dev={dev.id_dev} No previous events found for device {currentDeviceId} IP: {dev.base_url}, using 1 hour ago: {DateTimeOffset.FromUnixTimeMilliseconds(lastTimestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    logger.LogTrace($"264 id_dev={dev.id_dev} Старт сбор событий IP: {dev.base_url} начиная с метки времени {DateTimeOffset.FromUnixTimeMilliseconds(lastTimestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                }

                // Получаем события НАЧИНАЯ С последнего timestamp (включительно)
                var logsResponse = await dev.GetEvents(lastTimestamp, 50);

                if (logsResponse.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(logsResponse.Content))
                {
                    var logsData = JsonSerializer.Deserialize<Basip.LogsResponse>(logsResponse.Content);
                    //текущее время.
                    long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (logsData?.list_items != null && logsData.list_items.Count > 0)//если есть события, то начинаю обработку
                    {
                        logger.LogTrace($"276 id_dev={dev.id_dev} Получено {logsData.list_items.Count} событий от device base_url= {dev.base_url}");

                        long maxTimestamp = lastTimestamp;
                        int processedEventsCount = 0;
                        
                        // Обрабатываем все события НАЧИНАЯ С последнего timestamp
                        foreach (var logItem in logsData.list_items)
                        {
                            var currentStr = DateTimeOffset.FromUnixTimeMilliseconds(currentTimestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                            var logItemStr = DateTimeOffset.FromUnixTimeMilliseconds(logItem.timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                            var lastStr = DateTimeOffset.FromUnixTimeMilliseconds(lastTimestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

                            logger.LogDebug(
                                "319 id_dev={IdDev} параметры события и времена обработки currentTimestamp={currentTimestamp}, logItemTimestamp={logItemTimestamp}, lastTimestamp={lastTimestamp}",
                                dev.id_dev, currentStr, logItemStr, lastStr);

                            // Берем события с timestamp >= lastTimestamp
                            if (logItem.timestamp >= lastTimestamp)
                            {
                                if (logItem.timestamp > maxTimestamp)
                                {
                                    if (logItem.timestamp > currentTimestamp)
                                    {
                                        //метка времени превышает текущее время
                                        logger.LogInformation("336 id_dev={IdDev} IP: {BaseUrl} время события превышает текущую дату LogItem: {LogItem}", dev.id_dev, dev.base_url, JsonSerializer.Serialize(logItem));
                                        continue; // <-- пропускаем аномальное событие, продолжаем обработку списка

                                    }
                                    else
                                    {
                                        maxTimestamp = logItem.timestamp;//собираю максимальную метку времени, т.к. нет гарантии, что события идут последовательно.
                                    }
                                }
                                // Для отладки можно вывести полный JSON
                                logger.LogDebug("293 id_dev={IdDev} IP: {BaseUrl} текст события LogItem: {LogItem}", dev.id_dev, dev.base_url, JsonSerializer.Serialize(logItem));
                                await ProcessLogEvent(dbForEvents, dev, logItem, currentDeviceId, majorVersion);//записываю событий в базу данных СКУД
                                processedEventsCount++;//счетчик: сколько событий записано
                            }
                            else
                            {
                                logger.LogDebug($"297 id_dev={dev.id_dev} Skipping old event with timestamp: {logItem.timestamp} {DateTimeOffset.FromUnixTimeMilliseconds(logItem.timestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss} (last: {lastTimestamp} {DateTimeOffset.FromUnixTimeMilliseconds(lastTimestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss})");
                            }
                        }
                        logger.LogTrace($"300 id_dev={dev.id_dev} currentDeviceId = {currentDeviceId} maxTimestamp= {maxTimestamp} {DateTimeOffset.FromUnixTimeMilliseconds(maxTimestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                        if (processedEventsCount > 0)
                        {
                            dbForEvents.SetLastEventID(currentDeviceId, maxTimestamp);//запись последней метки времени в базу данных. Дальше опрос начнется с этой метки
                            logger.LogDebug($"304 id_dev={dev.id_dev} Обработано {processedEventsCount} событий, последняя метка времени to: {DateTimeOffset.FromUnixTimeMilliseconds(maxTimestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss}");//фиксирую в логе сколько записей было сделано для указанного id_dev={dev.id_dev}

                            if (options.clear_log)
                            {
                                var statusLog = await dev.ClearingLog(majorVersion);
                                switch (statusLog.StatusCode)
                                {
                                    case HttpStatusCode.OK:
                                        logger.LogInformation($"312 Successful attempt to clear the event log IP: {dev.base_url}, device {currentDeviceId},id_dev={dev.id_dev},  major api = {majorVersion}");
                                        break;
                                    default:
                                        logger.LogWarning($"315 Failed attempt to clear the event log IP: {dev.base_url}, device {currentDeviceId}, major api = {majorVersion}");
                                        break;
                                }
                            }
                            else
                            {
                                logger.LogWarning($"321 id_dev={dev.id_dev} The event log has not been cleared IP: {dev.base_url}, device {currentDeviceId}, major api = {majorVersion}");
                            }
                        }
                        else
                        {
                            logger.LogInformation($"326 id_dev={dev.id_dev} Нет новых события для IP: {dev.base_url}, device {currentDeviceId}, major api = {majorVersion}");
                        }
                    }
                    else
                    {
                        logger.LogDebug($"331 id_dev={dev.id_dev} No events found in response for device {currentDeviceId} ");
                    }
                }
                else
                {
                    logger.LogWarning($"336 id_dev={dev.id_dev} Failed to get events. Status: {logsResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"341 id_dev={dev.id_dev} Error processing events for device {currentDeviceId} IP: {dev.base_url}: {ex.Message}");
            }

            // ОБРАБОТКА КАРТ: создаем новый экземпляр DB для работы с картами
            DB dbForCards = new DB(options.db_config ?? string.Empty, logger);

            try
            {
                DataRowCollection cardList = dbForCards.GetCardForLoad(currentDeviceId).Rows;
                logger.LogInformation($"350 id_dev={dev.id_dev} Панель ID: {currentDeviceId}, IP: {dev.ip} - Card count: {cardList.Count}");

                foreach (DataRow card in cardList)
                {
                    switch ((int)card["operation"])
                    {
                        case 1: // Запись карты
                            await ProcessCardWrite(dbForCards, dev, card, currentDeviceId);
                            break;

                        case 2: // Удаление карты
                            await ProcessCardDelete(dbForCards, dev, card, currentDeviceId, major);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"368 id_dev={dev.id_dev} Error processing cards for device {currentDeviceId}: {ex.Message}");
            }
        }

        private async Task ProcessCardWrite(DB db, Device dev, DataRow card, int deviceId)
        {
            string cardId = GetCardId(card);
            logger.LogInformation($@"369 Command destination: writekey id_dev={deviceId} BASE_URL {dev.base_url} key=""{options.uidtransform(cardId)}"" AddCard ");

            RestResponse request = await dev.AddIdentifier(options.uidtransform(cardId), "card");
            bool shouldDeleteFromQueue = false;
            int targetDeviceId = (int)card["id_dev"];

            switch (request.StatusCode)
            {
                case HttpStatusCode.OK:
                    await ProcessCardWriteSuccess(db, dev, card, cardId, targetDeviceId, request);
                    shouldDeleteFromQueue = true;
                    break;

                case HttpStatusCode.BadRequest:
                    await ProcessCardAlreadyExists(db, dev, card, cardId, targetDeviceId);
                    shouldDeleteFromQueue = true;
                    break;

                default:
                    logger.LogInformation($@"394 Answer destination: writekey id_dev={targetDeviceId} BASE_URL {dev.base_url} Answer: {request.StatusCode} key=""{options.uidtransform(cardId)}""");
                    db.UpdateCardInDevIncrement((int)card["id_cardindev"]);
                    break;
            }

            // Удаляем из очереди если операция успешна или карта уже существует
            if (shouldDeleteFromQueue)
            {
                db.DeleteCardInDev((int)card["id_cardindev"]);
                logger.LogDebug($"403 Card {cardId} successfully processed and removed from queue for device {targetDeviceId}");
            }
        }

        private async Task ProcessCardWriteSuccess(DB db, Device dev, DataRow card, string cardId, int deviceId, RestResponse request)
        {
            try
            {
                var uid = JsonDocument.Parse(request.Content!).RootElement.GetProperty("uid").ToString();
                logger.LogInformation($@"412 Answer destination: writekey id_dev={deviceId} BASE_URL {dev.base_url} Answer: OK key=""{options.uidtransform(cardId)}"" uid={uid}");

                int uidInt = 0;
                if (!int.TryParse(uid, out uidInt))
                {
                    logger.LogWarning($"417 Cannot parse UID '{uid}' as integer, using 0");
                    uidInt = 0;
                }

                int rowsUpdated = db.FixCardIdxOK(cardId, deviceId, uidInt);

                if (rowsUpdated > 0)
                {
                    logger.LogInformation($"425 Successfully updated {rowsUpdated} rows in CARDIDX");
                }
                else
                {
                    logger.LogWarning($"429 No rows updated in CARDIDX for card {cardId}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"434 Error updating CARDIDX: {ex.Message}");
                // Фолбэк: пытаемся записать с uid=0
                try
                {
                    db.FixCardIdxOK(cardId, deviceId, 0);
                }
                catch { }
            }
        }

        private async Task ProcessCardAlreadyExists(DB db, Device dev, DataRow card, string cardId, int deviceId)
        {
            logger.LogInformation($@"446 Answer destination: writekey id_dev={deviceId} BASE_URL {dev.base_url} Answer: BAD REQUEST key=""{options.uidtransform(cardId)}"" card already exists");

            try
            {
                var cardInfoResponse = await dev.GetInfoCard(options.uidtransform(cardId), 2);
                int existingUid = 0;

                logger.LogInformation($"453 GetInfoCard response status: {cardInfoResponse.StatusCode}");

                if (cardInfoResponse.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(cardInfoResponse.Content))
                {
                    try
                    {
                        var cardInfo = JsonDocument.Parse(cardInfoResponse.Content);

                        if (cardInfo.RootElement.TryGetProperty("list_items", out var listItems) && listItems.GetArrayLength() > 0)
                        {
                            var firstItem = listItems[0];

                            if (firstItem.TryGetProperty("identifier_uid", out var uidProperty))
                            {
                                var uidStr = uidProperty.ToString();

                                if (int.TryParse(uidStr, out existingUid))
                                {
                                    logger.LogInformation($"471 CARD ALREADY EXISTS - Device: {dev.base_url}, id_dev={deviceId}, Card Number: {cardId}, Existing UID: {existingUid}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"478 Could not parse UID for existing card {cardId}: {ex.Message}");
                    }
                }

                int rowsUpdated = db.FixCardIdxOK(cardId, deviceId, existingUid);

                if (rowsUpdated > 0)
                {
                    logger.LogDebug($"486 Successfully updated CARDIDX for existing card UID: {existingUid}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"491 Error processing existing card: {ex.Message}");
            }
        }

        private async Task ProcessCardDelete(DB db, Device dev, DataRow card, int deviceId, int majorVersion)
        {
            string cardId = GetCardId(card);
            string delcommandlog = $@"deletekey id_dev ={deviceId} BASE_URL {dev.base_url} key = ""{options.uidtransform(cardId)}""";

            logger.LogInformation("503 "+delcommandlog + "GetInfoCard " + "major api=\"" + majorVersion + "\"");

            RestResponse? content = await dev.GetInfoCard(options.uidtransform(cardId), majorVersion);

            switch (content.StatusCode)
            {
                case HttpStatusCode.OK:
                    using (JsonDocument jsonDoc = JsonDocument.Parse(content.Content!))
                    {
                        // list_items может отсутствовать в ответе: GetProperty в этом случае
                        // выбрасывал KeyNotFoundException и карта оставалась в очереди навсегда.
                        if (!jsonDoc.RootElement.TryGetProperty("list_items", out JsonElement listItems) ||
                            listItems.ValueKind != JsonValueKind.Array)
                        {
                            logger.LogWarning($"547 {delcommandlog} Answer: ERR no list_items in response");
                            db.UpdateCardInDevIncrement((int)card["id_cardindev"]);
                            return;
                        }

                        // Ищем точное совпадение номера карты
                        string? uidToDelete = null;
                        foreach (JsonElement element in listItems.EnumerateArray())
                        {
                            if (element.TryGetProperty("identifier_number", out JsonElement number) &&
                                number.ToString() == options.uidtransform(cardId) &&
                                element.TryGetProperty("identifier_uid", out JsonElement uidElement))
                            {
                                uidToDelete = uidElement.ToString();
                                break;
                            }
                        }

                        if (uidToDelete != null)
                        {
                            logger.LogInformation($"528 {delcommandlog}" + $@" для карты {cardId} uid ={uidToDelete} DeleteCard major api = {majorVersion}");

                            var status = (await dev.DeleteCard(uidToDelete)).StatusCode;

                            switch (status)
                            {
                                case HttpStatusCode.OK:
                                    logger.LogDebug($@"535 {delcommandlog}   Answer: OK uid={uidToDelete}");
                                    db.DeleteCardInDev((int)card["id_cardindev"]);
                                    logger.LogInformation($"537 Card {cardId} successfully processed and removed from queue for device {deviceId}");
                                    break;
                                default:
                                    logger.LogDebug($@"540 Answer destination: {delcommandlog} Answer: ERR uid={uidToDelete} no delete");
                                    db.UpdateCardInDevIncrement((int)card["id_cardindev"]);
                                    break;
                            }
                        }
                        else
                        {
                            logger.LogDebug($@"547 {delcommandlog} Answer: OK no card in panel");
                            db.DeleteCardInDev((int)card["id_cardindev"]);
                            logger.LogInformation($"Card {cardId} successfully processed and removed from queue for device {deviceId}");
                        }
                    }
                    break;

                default:
                    logger.LogError($@"554 {delcommandlog} Answer: ERR faild GetInfoCard (не удалось получить информацию о карте)");
                    db.UpdateCardInDevIncrement((int)card["id_cardindev"]);
                    break;
            }
        }
        private string BuildAboutString(JsonDocument deviceInfo, string auth)
        {
            if (deviceInfo == null)
                return string.Empty;

            var root = deviceInfo.RootElement;

            string deviceModel = GetJsonString(root, "device_model");
            string frameworkVersion = GetJsonString(root, "framework_version");
            string firmwareVersion = GetJsonString(root, "firmware_version");
            string apiVersion = GetJsonString(root, "api_version");


            string result = $"{deviceModel},{frameworkVersion},{firmwareVersion},{apiVersion}, {auth}";

            // Обрезаем, если больше 250 символов
            if (result.Length > 250)
            {
                result = result.Substring(0, 250);
                logger.LogWarning($"618 STRVALUE was truncated to 250 characters for device");
            }

            return result;
        }

        private string GetJsonString(JsonElement root, string propertyName)
        {
            if (root.TryGetProperty(propertyName, out JsonElement element))
            {
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? ""
                    : element.ToString();
            }
            return "";
        }
        
        //процедура выборки событий из панели
        private async Task ProcessLogEvent(DB db, Device dev, LogItem logEvent, int deviceId, int majorVersion)
        {

            try
            {
                // Номер идентификатора: у API 2.x — info.model.number, у API 1.x — info.number
                // (разбор обоих вариантов в LogItem.IdentifierNumber).
                string? cardNumber = logEvent.IdentifierNumber;

                // ПРАВИЛЬНЫЕ КОДЫ СОБЫТИЙ
                int eventCode = logEvent.name?.key switch
                {
                    "access_denied_by_unknown_card" => 46,
                    "access_granted_by_valid_identifier" => 50,
                  //  "lock_was_opened_by_exit_btn" => 49,
                  //  "successful_login_api_call" => 49,
                    "access_granted_by_call_host" => 51,
                    "access_denied_by_not_valid_input_code" => 91,
                    "access_granted_by_master_code" => 90,
                    _ => 0 // для остальных событий
                };

                //// Тип события для информации

                string eventType = logEvent.name?.key ?? "Unknown";
                DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.timestamp).LocalDateTime;
               
                string note = $"Device=\"{dev.name}\", Type={eventType} " +
                              $"Readdate=#{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}# " +
                              $"DeviceDate =#{dateTime:yyyy-MM-dd HH:mm:ss}# " +
                              $"RowDeviceDate = {logEvent.timestamp}";

                if (eventCode > 0)
                {
                    
                    int? insertedEventTypeId = db.EventInsert(
                        id_db: 1,
                        id_eventtype: eventCode,
                        id_cntrl: dev.ctrl,
                        id_reader: 0,
                        note: cardNumber,
                        time: dateTime,
                        id_video: null,
                        id_user: null,
                        ess1: null,
                        ess2: null,
                        idsource: 1,
                        idserverts: logEvent.timestamp
                    );

                    int insertedId = insertedEventTypeId ?? 0;

                    if (eventType != "successful_login_api_call")
                    {
                        if (cardNumber != null)
                        {
                            logger.LogInformation($"703 +{note}, Card={cardNumber}, EventType={eventCode}, Id_Event={insertedId},  major api = {majorVersion}, id_dev={dev.id_dev}");
                        }
                        else
                        {
                            logger.LogInformation($"711 +{note}, EventType={eventCode}, Id_Event={insertedId}, major api = {majorVersion}");
                        }
                    }

                    if (insertedEventTypeId.HasValue)
                    {
                        logger.LogInformation($"718 Event saved: Dev={deviceId}, Type={eventCode}, EventType={eventCode}, Id_Event={insertedEventTypeId.Value}, major api = {majorVersion}");
                    }
                    else
                    {
                        logger.LogError($"722 Failed to save event: Dev={deviceId}, EventType={eventCode}, major api = {majorVersion} ");
                    }
                } else
                {
                    logger.LogInformation($"741 -{note}, Card={cardNumber}, EventType={eventCode}, Id_Event=,  major api = {majorVersion}, id_dev={dev.id_dev}");
                }
            }

            catch (Exception ex)
            {
                logger.LogError($"729 Error: {ex.Message}");
            }
        }

        // Номер карты из строки очереди. Раньше здесь был прямой ToString(),
        // который на DBNull давал null и приводил к риску NullReference.
        private static string GetCardId(DataRow card)
        {
            return card["id_card"] == DBNull.Value ? string.Empty : card["id_card"].ToString() ?? string.Empty;
        }
    }
}
