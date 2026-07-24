using FirebirdSql.Data.FirebirdClient;
using System.Data;
using static System.Runtime.InteropServices.JavaScript.JSType;

public class DB
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    public DB(string connectionString, ILogger? logger = null)
    {
        _logger = logger;

        // ВАЛИДАЦИЯ ДО ВСЕГО ОСТАЛЬНОГО
        _connectionString = ValidateAndFixConnectionString(connectionString);
    }

    public DB(string connectionString) : this(connectionString, null)
    {
    }

    private string ValidateAndFixConnectionString(string connectionString)
    {

       
        // 1. ПРОВЕРКА НА NULL И ПУСТУЮ СТРОКУ
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Логируем критическую ошибку
            _logger?.LogCritical("Connection string is null or empty! Please check appsettings.json");

            // Выбрасываем понятное исключение с инструкцией
            throw new InvalidOperationException(
                "Database connection string is not configured. " +
                "Please add 'db_config' to 'Service' section in appsettings.json.\n" +
                "Example: \"db_config\": \"User=SYSDBA;Password=temp;Database=D:\\testdb\\hl1\\shieldpro_rest.gdb;DataSource=127.0.0.1;Port=3050;\""
            );
        }

        // 2. Убираем лишние кавычки (проблема в вашем appsettings.json)
        string fixedString = connectionString.Trim('"');
        if (fixedString != connectionString)
        {
            _logger?.LogWarning("Connection string had extra quotes, fixed automatically");
            connectionString = fixedString;
        }

        // 3. Проверяем наличие обязательных параметров
        var requiredParams = new[] { "User", "Password", "Database", "DataSource" };
        var missingParams = new List<string>();

        foreach (var param in requiredParams)
        {
            if (!connectionString.Contains(param, StringComparison.OrdinalIgnoreCase))
            {
                missingParams.Add(param);
            }
        }

        if (missingParams.Any())
        {
            // Если нет Database или DataSource - критично
            if (missingParams.Contains("Database") || missingParams.Contains("DataSource"))
            {
                throw new InvalidOperationException(
                    $"Connection string missing critical parameters: {string.Join(", ", missingParams)}\n" +
                    $"Current string: {connectionString}"
                );
            }

            _logger?.LogWarning(
                "Connection string missing recommended parameters: {MissingParams}",
                string.Join(", ", missingParams)
            );
        }

        // 4. Проверка существования файла БД
        string databasePath = ExtractDatabasePath(connectionString);
        if (!string.IsNullOrEmpty(databasePath))
        {
            if (!File.Exists(databasePath))
            {
                _logger?.LogWarning(
                    "82 Database file does not exist at: {Path}. Please verify the path is correct.",
                    databasePath
                );
                throw new FileNotFoundException(
                    $"Database file not found: {databasePath}. " +
                    "Please verify the path in appsettings.json is correct."
                );
            }
        }
        
            var fileInfo = new FileInfo(databasePath);
            _logger?.LogInformation(
                "96 Database file found. Size: {Size} MB",
                fileInfo.Length / 1024 / 1024
            );
        

        _logger?.LogInformation("Connection string validation completed successfully");
        return connectionString;
    }

    private string ExtractDatabasePath(string connectionString)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                connectionString,
                @"Database\s*=\s*([^;]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            return match.Success ? match.Groups[1].Value.Trim().Trim('"') : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private FbConnection CreateConnection()
    {
        return new FbConnection(_connectionString);
    }

    // Метод для проверки существования таблицы в базе данных
    public bool TableExists(string tableName)
    {
        using var con = CreateConnection();
        try
        {
            con.Open();
            string sql = $@"SELECT COUNT(*) 
                           FROM RDB$RELATIONS 
                           WHERE RDB$RELATION_NAME = '{tableName.ToUpper()}' 
                           AND RDB$SYSTEM_FLAG = 0";

            using var command = new FbCommand(sql, con);
            var result = command.ExecuteScalar();
            return Convert.ToInt32(result) > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при проверке таблицы {tableName}: {ex.Message}");
            return false;
        }
    }

    // Проверка всех обязательных таблиц
    public bool CheckRequiredTables(ILogger logger)
    {
        logger.LogInformation($"Попытка подключения к БД. Строка подключения: {_connectionString}");

        using var con = CreateConnection();
        try
        {
            con.Open();
            logger.LogInformation("Подключение к базе данных успешно установлено.");

            string[] requiredTables = {
            "DEVICE",
            "BAS_PARAM",
            "CARDINDEV",
            "CARD",
            "CARDIDX"
        };

            logger.LogInformation($"Начинаю проверку обязательных таблиц: {string.Join(", ", requiredTables)}");

            var missingTables = new List<string>();

            foreach (var table in requiredTables)
            {
                bool exists = TableExists(table);
                logger.LogDebug($"Таблица '{table}': {(exists ? "НАЙДЕНА" : "ОТСУТСТВУЕТ")}"); // 3. Логируем результат для каждой таблицы
                if (!exists)
                {
                    missingTables.Add(table);
                }
            }

            if (missingTables.Any())
            {
                // 4. Используем logger вместо Console.WriteLine
                logger.LogCritical($"КРИТИЧЕСКАЯ ОШИБКА: В базе данных отсутствуют обязательные таблицы: {string.Join(", ", missingTables)}");
                return false;
            }

            logger.LogInformation("Проверка обязательных таблиц завершена успешно. Все таблицы найдены.");
            return true;
        }
        catch (Exception ex)
        {
            // 5. Логируем саму ошибку подключения
            logger.LogError($"ОШИБКА ПОДКЛЮЧЕНИЯ К БАЗЕ ДАННЫХ: {ex.Message}");
            return false;
        }
    }

    public bool InsertAbout(int id_dev, string strValue)
    {
        using var con = CreateConnection();
        try
        {
            con.Open();

            string deleteSql = @"delete from bas_param bp 
                                where bp.id_dev = @id_dev 
                                and (bp.param = 'about'
                                or bp.param = 'ABOUT'
                                )";

            using var deleteCommand = new FbCommand(deleteSql, con);
            deleteCommand.Parameters.AddWithValue("@id_dev", id_dev);
            deleteCommand.ExecuteNonQuery();

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, STRVALUE) 
                                VALUES (@id_dev, 'ABOUT', @strvalue)";

            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@strvalue", strValue.ToString());
            insertCommand.ExecuteNonQuery();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting about: {ex.Message} for id_dev={id_dev}");
            return false;
        }
    }
    //установка состояния связи с панелью 0 - нет связи, 1 - есть связь
    public bool SetOnlineState(int id_dev, int state)
    {
        using var con = CreateConnection();
        try
        {
            con.Open();

            string deleteSql = @"delete from bas_param bp 
                                where bp.id_dev = @id_dev 
                                and bp.param = 'ONLINE'";

            using var deleteCommand = new FbCommand(deleteSql, con);
            deleteCommand.Parameters.AddWithValue("@id_dev", id_dev);
            deleteCommand.ExecuteNonQuery();

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE) 
                                VALUES (@id_dev, 'ONLINE', @state)";
            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@state", state);
            insertCommand.ExecuteNonQuery();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting last event ID: {ex.Message}");
            return false;
        }
    }

    public bool SetLastEventID(int id_dev, long eventId)
    {
        using var con = CreateConnection();
        try
        {
            con.Open();

            string deleteSql = @"delete from bas_param bp 
                                where bp.id_dev = @id_dev 
                                and bp.param = 'LASTEVENT'";

            using var deleteCommand = new FbCommand(deleteSql, con);
            deleteCommand.Parameters.AddWithValue("@id_dev", id_dev);
            deleteCommand.ExecuteNonQuery();

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, STRVALUE) 
                                VALUES (@id_dev, 'LASTEVENT', @eventId)";

            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@eventId", eventId.ToString());
            insertCommand.ExecuteNonQuery();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting last event ID: {ex.Message}");
            return false;
        }
    }

    public long GetLastEventID(int id_dev)
    {
        using var con = CreateConnection();
        try
        {
            con.Open();

            string sql = @"SELECT bp.STRVALUE 
                          FROM bas_param bp 
                          WHERE bp.id_dev = @id_dev 
                          AND bp.param = 'LASTEVENT'";

            using var command = new FbCommand(sql, con);
            command.Parameters.AddWithValue("@id_dev", id_dev);

            var result = command.ExecuteScalar();
            if (result != null && long.TryParse(result.ToString(), out long lastEventId))
            {
                return lastEventId;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting last event ID: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Вставка события в базу данных через хранимую процедуру DEVICEEVENTS_INSERT
    /// </summary>
    /// <returns>ID вставленного события или null при ошибке</returns>
    public int? EventInsert(
        int id_db = 1,
        int? id_eventtype = null,
        int? id_cntrl = null,
        int? id_reader = null,
        string note = null,
        DateTime? time = null,
        int? id_video = null,
        int? id_user = null,
        int? ess1 = null,
        int? ess2 = null,
        int? idsource = null,
        long? idserverts = null)
    {
        // Используем пул соединений (если включен в строке подключения)
        using var con = CreateConnection();

        try
        {
            // Открываем соединение с таймаутом
            con.Open();

            // Используем параметризованный запрос для защиты от SQL-инъекций
            const string sql = @"EXECUTE PROCEDURE DEVICEEVENTS_INSERT(
            @id_db, @id_eventtype, @id_cntrl, @id_reader, @note, @time, 
            @id_video, @id_user, @ess1, @ess2, @idsource, @idserverts)";

            using var command = new FbCommand(sql, con);

            // Настройка таймаута выполнения команды (30 секунд)
            command.CommandTimeout = 30;

            // Добавляем все параметры с явным указанием типов
            command.Parameters.AddWithValue("@id_db", id_db);

            // Для nullable параметров используем DBNull.Value если null
            command.Parameters.AddWithValue("@id_eventtype",
                id_eventtype ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_cntrl",
                id_cntrl ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_reader",
                id_reader ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@note",
                note ?? (object)DBNull.Value);

            // Обработка времени с проверкой
            string timeString;
            if (time.HasValue)
            {
                // Убеждаемся, что время в правильном формате для Firebird
                timeString = time.Value.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                timeString = "NOW"; // Используем NOW как строку, а не как функцию
            }
            command.Parameters.AddWithValue("@time", timeString);

            command.Parameters.AddWithValue("@id_video",
                id_video ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_user",
                id_user ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess1",
                ess1 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess2",
                ess2 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@idsource",
                idsource ?? 1);

            // Обработка timestamp с проверкой на отрицательные значения
            object serverTsValue = DBNull.Value;
            if (idserverts.HasValue && idserverts.Value > 0)
            {
                // Конвертируем миллисекунды в секунды и проверяем на переполнение
                long seconds = idserverts.Value / 1000;
                if (seconds > 0 && seconds <= int.MaxValue)
                {
                    serverTsValue = (int)seconds;
                }
            }
            command.Parameters.AddWithValue("@idserverts", serverTsValue);

            // Добавляем выходной параметр с явным указанием типа
            var outputParam = new FbParameter("@return_value", FbDbType.Integer)
            {
                Direction = ParameterDirection.ReturnValue
            };
            command.Parameters.Add(outputParam);

            // Выполняем команду
            int rowsAffected = command.ExecuteNonQuery();

            // Получаем возвращаемое значение
            if (outputParam.Value != DBNull.Value)
            {
                int result = Convert.ToInt32(outputParam.Value);

                // Логируем успешную вставку (если есть логгер)
                _logger?.LogDebug(
                    "Event inserted successfully. ID: {EventId}, Device: {DeviceId}, Type: {EventType}",
                    result, id_cntrl ?? 0, id_eventtype ?? 0);

                return result;
            }

            _logger?.LogWarning("Event inserted but no return value received");
            return null;
        }
        catch (FbException fbEx)
        {
            // Специфичная обработка ошибок Firebird
            _logger?.LogError(fbEx,
                "Firebird error inserting event. Device: {DeviceId}, EventType: {EventType}, ErrorCode: {ErrorCode}",
                id_cntrl ?? 0, id_eventtype ?? 0, fbEx.ErrorCode);

            // Проверяем на deadlock или timeout
            if (fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("timeout"))
            {
                // Можно добавить логику повторной попытки
                _logger?.LogWarning("Deadlock or timeout detected, event may need retry");
            }

            return null;
        }
        catch (InvalidOperationException invEx)
        {
            _logger?.LogError(invEx,
                "Invalid operation while inserting event. Device: {DeviceId}",
                id_cntrl ?? 0);
            return null;
        }
        catch (Exception ex)
        {
            // Общая ошибка
            _logger?.LogError(ex,
                "Unexpected error inserting event. Device: {DeviceId}, EventType: {EventType}",
                id_cntrl ?? 0, id_eventtype ?? 0);
            return null;
        }
        finally
        {
            // Соединение будет закрыто автоматически благодаря using
            // Но явно закроем, если соединение все еще открыто
            if (con.State != ConnectionState.Closed && con.State != ConnectionState.Broken)
            {
                con.Close();
            }
        }
    }
    public int? _EventInsert(int id_db = 1, int? id_eventtype = null, int? id_cntrl = null,
                           int? id_reader = null, string note = null, DateTime? time = null,
                           int? id_video = null, int? id_user = null, int? ess1 = null,
                           int? ess2 = null, int? idsource = null, long? idserverts = null)
    {
        using var con = CreateConnection(); 
        try
        {
            con.Open();

            string sql = @"EXECUTE PROCEDURE DEVICEEVENTS_INSERT(
            @id_db, @id_eventtype, @id_cntrl, @id_reader, @note, @time, 
            @id_video, @id_user, @ess1, @ess2, @idsource, @idserverts)";

            using var command = new FbCommand(sql, con);

            command.Parameters.AddWithValue("@id_db", id_db);
            command.Parameters.AddWithValue("@id_eventtype", id_eventtype ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_cntrl", id_cntrl ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_reader", id_reader ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@note", note ?? (object)DBNull.Value);

            string timeString = time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "NOW";
            command.Parameters.AddWithValue("@time", timeString);

            command.Parameters.AddWithValue("@id_video", id_video ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_user", id_user ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess1", ess1 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess2", ess2 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@idsource", idsource ?? 1);
            command.Parameters.AddWithValue("@idserverts", idserverts != null ? (long)(idserverts / 1000) : (object)DBNull.Value);

            // Добавляем выходной параметр
            var outputParam = new FbParameter("@return_value", FbDbType.Integer);
            outputParam.Direction = ParameterDirection.ReturnValue;
            command.Parameters.Add(outputParam);

            command.ExecuteNonQuery();

            // Получаем возвращаемое значение
            return outputParam.Value != DBNull.Value ? Convert.ToInt32(outputParam.Value) : (int?)null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting event: {ex.Message}");
            return null;
        }
    }
    public List<EventType> GetAllEventTypes()
    {
        var eventTypes = new List<EventType>();

        using var con = CreateConnection();
        try
        {
            con.Open();

            string sql = @"SELECT ID_EVENTTYPE, NAME 
                       FROM eventtype 
                       WHERE ACTIVE = 1 
                       ORDER BY ID_EVENTTYPE";

            using var command = new FbCommand(sql, con);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                eventTypes.Add(new EventType
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1)
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting event types: {ex.Message}");
        }

        return eventTypes;
    }

    // Класс для хранения данных о типе события
    public class EventType
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public DataTable GetDevice()
    {
        using var con = CreateConnection();
        con.Open();

        string sql = @"SELECT 
            d.id_dev, 
            bp.intvalue as IP,
            bp_login.strvalue as LOGIN,
            bp_pass.strvalue as PASS,
            d.id_ctrl as ctrl,
            SUBSTRING(d.NAME FROM 1 FOR 31) as CTRL_NAME
        FROM device d
        LEFT JOIN bas_param bp ON d.id_dev = bp.id_dev AND bp.param = 'IP'
        LEFT JOIN bas_param bp_login ON bp_login.id_dev = d.id_dev AND bp_login.param = 'LOGIN'
        LEFT JOIN bas_param bp_pass ON bp_pass.id_dev = d.id_dev AND bp_pass.param = 'PASS'
        WHERE bp.intvalue IS NOT NULL";

        using var getcomand = new FbCommand(sql, con);
        var reader = getcomand.ExecuteReader();
        DataTable table = new DataTable();
        table.Load(reader);
        return table;
    }

    public DataTable GetCardForLoad(int id_dev)
    {
        using var con = CreateConnection();
        con.Open();

        string sql = $@"select cd.id_cardindev, cd.id_card, cd.id_dev,cd.operation from cardindev cd
        join device d on d.id_dev=cd.id_dev
        join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader is null
        where d2.id_dev={id_dev}";

        using var getcomand = new FbCommand(sql, con);
        var reader = getcomand.ExecuteReader();
        DataTable table = new DataTable();
        table.Load(reader);
        return table;
    }

    public void DeleteCardInDev(int id_cardindev)
    {
        using var con = CreateConnection();
        con.Open();

        using var getcomand = new FbCommand($@"delete from cardindev cd where cd.id_cardindev ={id_cardindev}", con);
        getcomand.ExecuteNonQuery();
    }

    public void UpdateCardInDevIncrement(int id_cardindev)
    {
        using var con = CreateConnection();
        con.Open();

        using var getcomand = new FbCommand($@"update cardindev cd set cd.attempts=cd.attempts+1 where cd.id_cardindev={id_cardindev}", con);
        getcomand.ExecuteNonQuery();
    }

    public int FixCardIdxOK(string idCard, int idDev, int uid)
    {
        using var con = CreateConnection();
        con.Open();

        try
        {
            // Сначала пытаемся обновить существующую запись
            string updateSql = @"UPDATE CARDIDX SET
                DEVIDX = @uid,
                LOAD_TIME = CURRENT_TIMESTAMP,
                LOAD_RESULT = 'OK'
                WHERE (ID_CARD = @idCard) AND (ID_DEV = @idDev)";

            using var updateCommand = new FbCommand(updateSql, con);
            updateCommand.Parameters.AddWithValue("@uid", uid);
            updateCommand.Parameters.AddWithValue("@idCard", idCard);
            updateCommand.Parameters.AddWithValue("@idDev", idDev);

            int rowsUpdated = updateCommand.ExecuteNonQuery();

            // Если не нашли запись для обновления - вставляем новую
            if (rowsUpdated == 0)
            {
                string insertSql = @"INSERT INTO CARDIDX 
                    (ID_CARD, ID_DEV, DEVIDX, LOAD_TIME, LOAD_RESULT) 
                    VALUES (@idCard, @idDev, @uid, CURRENT_TIMESTAMP, 'OK')";

                using var insertCommand = new FbCommand(insertSql, con);
                insertCommand.Parameters.AddWithValue("@uid", uid);
                insertCommand.Parameters.AddWithValue("@idCard", idCard);
                insertCommand.Parameters.AddWithValue("@idDev", idDev);

                rowsUpdated = insertCommand.ExecuteNonQuery();
            }

            return rowsUpdated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in FixCardIdxOK: {ex.Message}");
            return 0;
        }
    }

    /* фиксация проблем для все карт в очереди, если панель по каким-то причинам недоступна
     * 
     */

    public int FixDeviceErr(string messErr, int idDev)
    {
        using var con = CreateConnection();
        con.Open();
        
        try
        {
            
            string updateSql = @"UPDATE CARDIDX cdx SET
                LOAD_TIME = CURRENT_TIMESTAMP,
                LOAD_RESULT = @messErr
                WHERE (ID_CARD not containing 'OK')
                AND (ID_DEV in(
                select d2.id_dev from device d
                join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader in (0,1)
                where d.id_dev=@idDev
                 ))";
            Console.WriteLine($"430 updateSql id_dev: {idDev}");
            using var updateCommand = new FbCommand(updateSql, con);
            updateCommand.Parameters.AddWithValue("@messErr", messErr);
            updateCommand.Parameters.AddWithValue("@idDev", idDev);
            int rowsUpdated = updateCommand.ExecuteNonQuery();

            return rowsUpdated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in FixCardIdxOK: {ex.Message}");
            return 0;
        }
    }
}
/* 12.03.2025 для всех карт для указанной панели добавить load_result как ошибка.
 * @input id_dev - id панели
 * @input messErr - сообщение, которое надо вписать в load_result
 * 
 */
/*
public void updateCaridxErrAll(int id_dev, string messErr) {

    string sql = $@"delete from bas_param bp where bp.id_dev={id_dev} and bp.param='{param_name}'";
    FbCommand getcomand = new FbCommand(sql, con);
    getcomand.ExecuteNonQuery();
    string data_int_ = (data_int == null) ? "NULL" : data_int.ToString();
    sql = $@"INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE, STRVALUE) VALUES ({id_dev},'{param_name}',{data_int_},'{data_string}')";
    getcomand = new FbCommand(sql, con);
    getcomand.ExecuteNonQuery();
}*/