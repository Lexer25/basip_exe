using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using RestSharp;
using Serilog.Core;
using Serilog.Events;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using static Basip.WorkerOptions;

namespace Basip
{
    public class Worker : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;

        private readonly string version;

        public Worker(ILogger<Worker> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;

            version = GetApplicationVersion();

            var time = options.timeout.Split(':');
            timeout = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            time = options.timeout.Split(':');
            timestart = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        // Получение версии приложения
        private string GetApplicationVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();


                // 2. AssemblyVersion (стандартный способ)
                var version = assembly.GetName().Version?.ToString();
                if (!string.IsNullOrEmpty(version) && version != "0.0.0.0")
                    return version;

                // 3. Из файла сборки
                if (!string.IsNullOrEmpty(assembly.Location))
                {
                    var fileInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
                    if (!string.IsNullOrEmpty(fileInfo.ProductVersion))
                        return fileInfo.ProductVersion;
                    if (!string.IsNullOrEmpty(fileInfo.FileVersion))
                        return fileInfo.FileVersion;
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation($"65 Version detection: {ex.Message}");
            }

            return "2.0.0"; // Значение по умолчанию
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(@$"32 basip start: {timestart} deltasleep: {deltasleep}");
            logger.LogInformation(@$"33 Service basip write and delete card started");
            await Task.Delay(deltasleep);

            // Выполняем инициализацию БД и получение устройств один раз при старте
            DB db = null;
            DataRowCollection devices = null;

            try
            {
                logger.LogInformation($"84 Версия приложения basip: {version}");

                // Создаем экземпляр DB с connection string
                db = new DB(options.db_config);

                // Проверяем наличие обязательных таблиц
                if (!db.CheckRequiredTables(logger))
                {
                    logger.LogCritical("92 Служебные таблицы не найдены. Программа завершает работу.");
                    Environment.Exit(1);
                }

                logger.LogInformation("96 Ok connect database");

                // Получаем устройства один раз при старте
                devices = db.GetDevice().Rows;
                logger.LogInformation("71 Зарегистрировано панелей bas-ip: " + devices.Count + " шт.");
            }
            catch (Exception e)
            {
                logger.LogError("104 No connect database: " + options.db_config);
                logger.LogError(e.ToString());
                Environment.Exit(1);
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

                logger.LogInformation($@"126 timeout basip: {timeout}");
                await Task.Delay(timeout, stoppingToken);
            }

            logger.LogCritical(@$"49 basip stop");
        }

        private void run(DB db)
        {

            DataRowCollection devices = db.GetDevice().Rows;
            List <Task> tasks = new List<Task>();
            Stopwatch stopwatch = Stopwatch.StartNew();

            int validDevicesCount = 0;
            logger.LogInformation("70 Start async.");

            foreach (DataRow row in devices)
            {
                try
                {
                    if (row["IP"] == DBNull.Value || Convert.ToInt32(row["IP"]) == 0)
                    {
                        logger.LogDebug($"149-0 Skipping device ID {row["id_dev"]} - no IP address");
                        continue;
                    }
                    logger.LogDebug($"149-1  device ID {row["id_dev"]} IP {row["id_dev"]} is OK");
                    Device device = new Device(row, options.time_wait_http);
                    validDevicesCount++;
                    logger.LogDebug($"149-2 start TaskGet ID {row["id_dev"]}");
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

            Task.WaitAll(tasks.ToArray());
        }

        // Изменяем TaskGet, чтобы он принимал DB
        private async Task TaskGet(DataRow row, DB dbForEvents)
        {
            String auth = "Auth OK";
            int currentDeviceId = (int)row["id_dev"];
           
            Device dev = new Device(row, options.time_wait_http);

            JsonDocument deviceInfo = await dev.GetInfo();
            if (deviceInfo == null)
            {
                logger.LogInformation($"192 id_dev={dev.id_dev} IP {dev.base_url} - нет связи. ");
                dbForEvents.FixDeviceErr("Device not connected", currentDeviceId);
                dbForEvents.SetOnlineState(currentDeviceId, 0);
                return;
            }
            
            if (!await dev.Auth())
            {
                auth = "Auth ERR";
                
            }

            // === НОВЫЙ КОД: Формирование STRVALUE ===
            string strValue = BuildAboutString(deviceInfo, auth);
            logger.LogInformation($"202 id_dev={dev.id_dev} Device d_dev={dev.id_dev} {currentDeviceId} ({dev.base_url})  STRVALUE for 'about': {strValue}");
            // Записываем значенме в базу данных
            dbForEvents.InsertAbout(currentDeviceId, strValue);
            dbForEvents.SetOnlineState(currentDeviceId, 1);

            // =======================================

            if (!await dev.Auth())
            {
                logger.LogInformation($"211 id_dev={dev.id_dev} Device  {dev.base_url} auth failed");
                dbForEvents.FixDeviceErr("Auth ERR", currentDeviceId);
                return;
            }
            if (!dev.is_online)
            {
                logger.LogDebug($"216 id_dev={dev.id_dev} Device {dev.base_url} offline");
                dbForEvents.FixDeviceErr("Devcice offline", currentDeviceId);
                return;
            }

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

            //контроле версии API. С версиями 3.Х и выше не работаем
            if (major > 3 || (major == 3 && middle > 00))
            {
                logger.LogWarning($"245 id_dev={dev.id_dev} Device {dev.base_url} apiVersion version {_apiVersion} is higher than allowed (3.x). Stopping work with this panel.");
                return;
            }

            // Используем переданный dbForEvents вместо создания нового
            try
            {
                string apiVersion = deviceInfo.RootElement.GetProperty("api_version").ToString();
                int majorVersion = int.Parse(apiVersion.Split('.')[0]);

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

                    if (logsData?.list_items != null && logsData.list_items.Count > 0)//если есть события, то начинаю обработку
                    {
                        logger.LogTrace($"276 id_dev={dev.id_dev} Получено {logsData.list_items.Count} событий от device base_url= {dev.base_url}");

                        long maxTimestamp = lastTimestamp;
                        int processedEventsCount = 0;

                        // Обрабатываем все события НАЧИНАЯ С последнего timestamp
                        foreach (var logItem in logsData.list_items)
                        {
                            // Берем события с timestamp >= lastTimestamp
                            if (logItem.timestamp >= lastTimestamp)
                            {
                                if (logItem.timestamp > maxTimestamp)
                                {
                                    maxTimestamp = logItem.timestamp;//собираю максимальную метку времени, т.к. нет гарантии, что события идут последовательно.
                                }
                                // Для отладки можно вывести полный JSON
                logger.LogDebug("293 id_dev={dev.id_dev} IP: {dev.base_url} текст события LogItem: {LogItem}", JsonSerializer.Serialize(logItem));
                                await ProcessLogEvent(dbForEvents, dev, logItem, currentDeviceId, deviceInfo);//записываю событий в базу данных СКУД
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
            DB dbForCards = new DB(options.db_config);

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
                            await ProcessCardDelete(dbForCards, dev, card, currentDeviceId, deviceInfo);
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
            string cardId = card["id_card"].ToString();
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
                var uid = JsonDocument.Parse(request.Content).RootElement.GetProperty("uid").ToString();
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

        private async Task ProcessCardDelete(DB db, Device dev, DataRow card, int deviceId, JsonDocument deviceInfo)
        {
            string cardId = card["id_card"].ToString();
            string delcommandlog = $@"deletekey id_dev ={deviceId} BASE_URL {dev.base_url} key = ""{options.uidtransform(cardId)}""";

            string apiVersion = deviceInfo.RootElement.GetProperty("api_version").ToString();
            int majorVersion = int.Parse(apiVersion.Split('.')[0]);

            logger.LogInformation("503 "+delcommandlog + "GetInfoCard " + "api_version=\"" + apiVersion + "\"");

            RestResponse? content = await dev.GetInfoCard(options.uidtransform(cardId), majorVersion);

            switch (content.StatusCode)
            {
                case HttpStatusCode.OK:
                    JsonDocument jsonDoc = JsonDocument.Parse(content.Content);
                    JsonElement.ArrayEnumerator jsonlist = jsonDoc.RootElement.GetProperty("list_items").EnumerateArray();

                    /*logger.LogDebug($"{delcommandlog} Получен ответ от устройства c мажорной версией api = {majorVersion}: {content.Content}");*/

                    // Ищем точное совпадение номера карты
                    string uidToDelete = null;
                    foreach (JsonElement element in jsonlist)
                    {
                        if (element.GetProperty("identifier_number").ToString() == options.uidtransform(cardId))
                        {
                            uidToDelete = element.GetProperty("identifier_uid").ToString();
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
                    break;

                default:
                    logger.LogError($@"554 {delcommandlog} Answer: ERR faild GetInfoCard (не удалось получить информацию о карте)");
                    db.UpdateCardInDevIncrement((int)card["id_cardindev"]);
                    break;
            }
        }


        // Добавь это поле в класс (например, Dictionary для маппинга событий)
        private static Dictionary<string, int> _eventTypeMapping;

        // Метод для инициализации маппинга из таблицы eventtype
        private void InitializeEventTypeMapping(DB db)
        {
            if (_eventTypeMapping != null) return;

            _eventTypeMapping = new Dictionary<string, int>();

            // Здесь нужно загрузить данные из таблицы eventtype
            // Предполагаю, что у тебя есть метод для получения всех eventtype из БД
            var eventTypes = db.GetAllEventTypes(); // Тебе нужно реализовать этот метод

            foreach (var eventType in eventTypes)
            {
                // Сопоставляем NAME из таблицы eventtype с ключами событий
                // Нужно преобразовать NAME в формат, который используется в logEvent.name.key
                string key = ConvertNameToKey(eventType.Name);
                if (!string.IsNullOrEmpty(key))
                {
                    _eventTypeMapping[key] = eventType.Id;
                }
            }
        }

        // Вспомогательный метод для преобразования NAME в ключ события
        private string ConvertNameToKey(string eventName)
        {
            return eventName?.ToLower()
                .Replace(" ", "_")
                .Replace("ё", "е")
                .Replace("неизвестная_карточка", "access_denied_by_unknown_card")
                .Replace("действительная_карточка", "access_granted_by_valid_identifier")
                // Добавь другие преобразования по необходимости
                ?? string.Empty;
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
        private async Task ProcessLogEvent(DB db, Device dev, LogItem logEvent, int deviceId, JsonDocument deviceInfo)
        {

            try
            {
                string apiVersion = deviceInfo.RootElement.GetProperty("api_version").ToString();
                int majorVersion = int.Parse(apiVersion.Split('.')[0]);

                string cardNumber = null;
                if (logEvent.info?.model != null)


                {
                    // Пробуем разные ключи, которые могут содержать номер карты
                    if (logEvent.info.model.ContainsKey("card"))
                    {
                        cardNumber = logEvent.info.model["card"]?.ToString();
                    }
                    else if (logEvent.info.model.ContainsKey("number"))
                    {
                        cardNumber = logEvent.info.model["number"]?.ToString();
                    }

                }

                // ПРАВИЛЬНЫЕ КОДЫ СОБЫТИЙ
                int eventCode = logEvent.name?.key switch
                {
                    "access_denied_by_unknown_card" => 46,
                    "access_granted_by_valid_identifier" => 50,
                    _ => 0 // для остальных событий
                };

                //// Тип события для информации

                string eventType = logEvent.name?.key ?? "Unknown";
                //DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.timestamp).DateTime;
                DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.timestamp).LocalDateTime;
               
                string note = $"Device=\"{dev.name}\", Type={eventType} " +
                              $"Readdate=#{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}# " +
                              $"DeviceDate =#{dateTime:yyyy-MM-dd HH:mm:ss}# " + // <-- явный формат + UTC
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

                    if (eventType != "successful_login_api_call")
                    {
                        if (majorVersion == 1 || majorVersion == 2)
                        {
                            if (cardNumber != null)
                            {
                                logger.LogInformation($"+{note}, Card={cardNumber}, EventType={eventCode}, Id_Event={insertedEventTypeId.Value},  major api = {majorVersion}");
                            }
                            else
                            {
                                logger.LogInformation($"+{note}, EventType={eventCode}, Id_Event={insertedEventTypeId.Value}, major api = {majorVersion}");
                            }
                        }
                        else
                        {
                            logger.LogCritical($"712 Not implemented with major api = {majorVersion}");
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
                }
            }

            catch (Exception ex)
            {
                logger.LogError($"729 Error: {ex.Message}");
            }
        }
    }
}