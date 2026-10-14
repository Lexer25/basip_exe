using FirebirdSql.Data.FirebirdClient;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public class DB
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    public DB(string connectionString, ILogger? logger = null)
    {
        _logger = logger;
        _connectionString = ValidateAndFixConnectionString(connectionString);
    }

    public DB(string connectionString) : this(connectionString, null)
    {
    }

    // =====================================================================
    // ЛОГИРОВАНИЕ SQL С РЕАЛЬНЫМИ ЗНАЧЕНИЯМИ ПАРАМЕТРОВ
    // =====================================================================

    /// <summary>
    /// Строит SQL-строку с уже подставленными значениями параметров.
    /// ВНИМАНИЕ: это ТОЛЬКО для логирования. Реальный запрос уходит с параметрами.
    /// </summary>
    private static string BuildSqlWithParams(string sql, FbCommand? command)
    {
        if (command == null || command.Parameters.Count == 0)
            return sql;

        var result = new StringBuilder(sql);

        // Сортируем параметры по убыванию длины имени, чтобы @id не затирал @id_dev
        var parameters = command.Parameters
            .Cast<FbParameter>()
            .Where(p => p.Direction != ParameterDirection.ReturnValue)
            .OrderByDescending(p => p.ParameterName.Length)
            .ToList();

        foreach (var p in parameters)
        {
            string literal = FormatSqlLiteral(p.Value);
            string bareName = p.ParameterName.TrimStart('@', ':');

            foreach (var prefix in new[] { "@", ":" })
            {
                string token = prefix + bareName;
                int idx = 0;
                while ((idx = result.ToString().IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int before = idx - 1;
                    int after = idx + token.Length;

                    bool leftOk = before < 0 ||
                                  !(char.IsLetterOrDigit(result[before]) || result[before] == '_');
                    bool rightOk = after >= result.Length ||
                                   !(char.IsLetterOrDigit(result[after]) || result[after] == '_');

                    if (leftOk && rightOk)
                    {
                        result.Remove(idx, token.Length);
                        result.Insert(idx, literal);
                        idx += literal.Length;
                    }
                    else
                    {
                        idx += token.Length;
                    }
                }
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Форматирует значение параметра как SQL-литерал.
    /// </summary>
    private static string FormatSqlLiteral(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        switch (value)
        {
            case string s:
                return "'" + s.Replace("'", "''") + "'";

            case DateTime dt:
                return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";

            case DateTimeOffset dto:
                return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + "'";

            case bool b:
                return b ? "1" : "0";

            case byte[] bytes:
                return "x'" + BitConverter.ToString(bytes).Replace("-", "") + "'";

            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);

            default:
                return "'" + value.ToString()!.Replace("'", "''") + "'";
        }
    }

    /// <summary>
    /// Логирование SQL-запроса с реальными значениями параметров.
    /// Уровень Debug — чтобы не засорять боевой журнал.
    /// </summary>
    private void LogSql(string sql, FbCommand? command = null, string? context = null)
    {
        if (_logger == null) return;

        string finalSql = BuildSqlWithParams(sql, command);

        var sb = new StringBuilder();
        sb.Append("SQL");
        if (!string.IsNullOrEmpty(context))
            sb.Append($" [{context}]");
        sb.Append(": ");
        sb.Append(finalSql.Replace("\r", " ").Replace("\n", " ").Trim());

        _logger.LogDebug("{SqlLog}", sb.ToString());
    }

    /// <summary>
    /// Логирование результата выполнения SQL-запроса.
    /// </summary>
    private void LogSqlResult(string context, int rowsAffected, long elapsedMs)
    {
        if (_logger == null) return;
        _logger.LogDebug(
            "SQL [{Context}] completed: rows={Rows}, elapsed={Elapsed}ms",
            context, rowsAffected, elapsedMs);
    }

    /// <summary>
    /// Маскирование пароля в строке подключения для логов.
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;
        return Regex.Replace(
            connectionString,
            @"(Password\s*=\s*)([^;]*)",
            "$1***",
            RegexOptions.IgnoreCase);
    }

    // =====================================================================
    // ПРОВЕРКА СТРОКИ ПОДКЛЮЧЕНИЯ
    // =====================================================================

    private string ValidateAndFixConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger?.LogCritical("Connection string is null or empty!");
            throw new InvalidOperationException(
                "Database connection string is not configured. " +
                "Please add 'db_config' to 'Service' section in appsettings.json."
            );
        }

        string fixedString = connectionString.Trim('"');
        if (fixedString != connectionString)
        {
            _logger?.LogWarning("Connection string had extra quotes, fixed automatically");
            connectionString = fixedString;
        }

        var presentParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                presentParams.Add(pair.Substring(0, eq).Trim());
            }
        }

        var missingParams = new[] { "User", "Password", "Database", "DataSource" }
            .Where(p => !presentParams.Contains(p))
            .ToList();

        if (missingParams.Any())
        {
            if (missingParams.Contains("Database") || missingParams.Contains("DataSource"))
            {
                throw new InvalidOperationException(
                    $"Connection string missing critical parameters: {string.Join(", ", missingParams)}\n" +
                    $"Current string: {MaskConnectionString(connectionString)}"
                );
            }

            _logger?.LogWarning(
                "Connection string missing recommended parameters: {MissingParams}",
                string.Join(", ", missingParams)
            );
        }

        _logger?.LogInformation("210 Testing database connection...");

        try
        {
            using var con = new FbConnection(connectionString);
            con.Open();

            using var cmd = new FbCommand("SELECT 1 FROM RDB$DATABASE", con);
            cmd.ExecuteScalar();

            con.Close();
            _logger?.LogInformation("221 Database connection successful");
        }
        catch (FbException fbEx)
        {
            string errorDetails = DecodeFirebirdError(fbEx, connectionString);

            _logger?.LogError("================================================================================");
            _logger?.LogError("DATABASE CONNECTION FAILED!");
            _logger?.LogError("{ErrorDetails}", errorDetails);
            _logger?.LogError("================================================================================");
            _logger?.LogError("Connection string: {ConnectionString}", MaskConnectionString(connectionString));
            _logger?.LogError("================================================================================");

            throw new InvalidOperationException(
                $"Cannot connect to database: {fbEx.Message}",
                fbEx
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError("================================================================================");
            _logger?.LogError("DATABASE CONNECTION FAILED!");
            _logger?.LogError("Error: {Error}", ex.Message);
            _logger?.LogError("================================================================================");
            _logger?.LogError("Connection string: {ConnectionString}", MaskConnectionString(connectionString));
            _logger?.LogError("================================================================================");

            throw new InvalidOperationException(
                $"Cannot connect to database: {ex.Message}",
                ex
            );
        }

        _logger?.LogInformation("254 Connection string validation completed successfully");
        return connectionString;
    }

    private string DecodeFirebirdError(FbException fbEx, string connectionString)
    {
        var message = fbEx.Message;
        var builder = new StringBuilder();

        string databasePath = ExtractDatabasePath(connectionString);
        string dataSource = ExtractDataSource(connectionString);
        string port = ExtractPort(connectionString);

        if (message.Contains("Cannot find database") ||
            message.Contains("I/O error") ||
            message.Contains("CreateFile"))
        {
            builder.AppendLine("❌ THE DATABASE FILE WAS NOT FOUND OR CANNOT BE ACCESSED");
            builder.AppendLine();
            builder.AppendLine($"   Database path: {databasePath}");
            builder.AppendLine($"   Server:        {dataSource}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. The file does not exist on the server");
            builder.AppendLine("   2. The path is incorrect (check case sensitivity)");
            builder.AppendLine("   3. Firebird service does not have read/write permissions");
            builder.AppendLine("   4. The file is locked by another process");
            builder.AppendLine("   5. The file is on a network drive that is not accessible");
        }
        else if (message.Contains("connection refused") ||
                 message.Contains("unavailable") ||
                 message.Contains("network error"))
        {
            builder.AppendLine("❌ CANNOT CONNECT TO FIREBIRD SERVER");
            builder.AppendLine();
            builder.AppendLine($"   Server: {dataSource}");
            builder.AppendLine($"   Port:   {port}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. Firebird service is not running on the server");
            builder.AppendLine("   2. Server is not reachable (check network/firewall)");
            builder.AppendLine("   3. Wrong port number (default is 3050)");
            builder.AppendLine("   4. Firebird is configured to use a different port");
            builder.AppendLine("   5. Firebird is configured for local connections only");
        }
        else if (message.Contains("password") ||
                 message.Contains("login") ||
                 message.Contains("authentication"))
        {
            builder.AppendLine("❌ AUTHENTICATION FAILED");
            builder.AppendLine();
            builder.AppendLine($"   User: {ExtractUser(connectionString)}");
            builder.AppendLine($"   Server: {dataSource}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. Wrong username or password");
            builder.AppendLine("   2. User does not have access to the database");
            builder.AppendLine("   3. User is not defined in Firebird security database");
            builder.AppendLine("   4. Password has been changed");
        }
        else if (message.Contains("database shutdown"))
        {
            builder.AppendLine("❌ DATABASE IS IN SHUTDOWN STATE");
            builder.AppendLine();
            builder.AppendLine($"   Database: {databasePath}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. Database was manually shut down");
            builder.AppendLine("   2. Database is being backed up");
            builder.AppendLine("   3. Database is being restored");
            builder.AppendLine("   4. Database is being maintained");
        }
        else if (message.Contains("version"))
        {
            builder.AppendLine("❌ INCOMPATIBLE DATABASE VERSION");
            builder.AppendLine();
            builder.AppendLine($"   Database: {databasePath}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. Database was created with newer Firebird version");
            builder.AppendLine("   2. Client library version mismatch");
            builder.AppendLine("   3. Database needs to be backed up and restored");
        }
        else if (message.Contains("corrupt") ||
                 message.Contains("invalid"))
        {
            builder.AppendLine("❌ DATABASE IS CORRUPTED OR INVALID");
            builder.AppendLine();
            builder.AppendLine($"   Database: {databasePath}");
            builder.AppendLine();
            builder.AppendLine("   Possible causes:");
            builder.AppendLine("   1. Database file is corrupted");
            builder.AppendLine("   2. Database needs to be validated/repaired");
            builder.AppendLine("   3. File system error");
        }
        else
        {
            builder.AppendLine("❌ DATABASE CONNECTION ERROR");
            builder.AppendLine();
            builder.AppendLine($"   Error: {message}");
            builder.AppendLine($"   Server: {dataSource}");
            builder.AppendLine($"   Database: {databasePath}");
        }

        builder.AppendLine();
        builder.AppendLine("   💡 TROUBLESHOOTING:");
        builder.AppendLine("   1. Check Firebird service is running");
        builder.AppendLine("   2. Test connection with FlameRobin or ISQL");
        builder.AppendLine("   3. Verify network connectivity and firewall settings");
        builder.AppendLine("   4. Check file permissions on the database file");
        builder.AppendLine("   5. Review Firebird log files for more details");

        return builder.ToString();
    }

    public bool TestConnection(out string errorMessage)
    {
        errorMessage = string.Empty;
        using var con = CreateConnection();
        try
        {
            _logger?.LogInformation("375 Testing database connection...");
            con.Open();

            using var cmd = new FbCommand("SELECT 1 FROM RDB$DATABASE", con);
            cmd.ExecuteScalar();

            con.Close();
            _logger?.LogInformation("382 Database connection test successful");
            return true;
        }
        catch (FbException fbEx)
        {
            errorMessage = DecodeFirebirdError(fbEx, _connectionString);
            _logger?.LogError(fbEx, "Database connection test failed");
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger?.LogError(ex, "Database connection test failed");
            return false;
        }
    }

    // =====================================================================
    // ИЗВЛЕЧЕНИЕ ПАРАМЕТРОВ СТРОКИ ПОДКЛЮЧЕНИЯ
    // =====================================================================

    private string ExtractDatabasePath(string connectionString)
    {
        try
        {
            var match = Regex.Match(connectionString, @"Database\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim().Trim('"') : "not specified";
        }
        catch { return "unknown"; }
    }

    private string ExtractDataSource(string connectionString)
    {
        try
        {
            var match = Regex.Match(connectionString, @"DataSource\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim().Trim('"') : "not specified";
        }
        catch { return "unknown"; }
    }

    private string ExtractPort(string connectionString)
    {
        try
        {
            var match = Regex.Match(connectionString, @"Port\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim().Trim('"') : "3050 (default)";
        }
        catch { return "3050 (default)"; }
    }

    private string ExtractUser(string connectionString)
    {
        try
        {
            var match = Regex.Match(connectionString, @"User\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim().Trim('"') : "not specified";
        }
        catch { return "unknown"; }
    }

    private FbConnection CreateConnection()
    {
        return new FbConnection(_connectionString);
    }

    // =====================================================================
    // ПРОВЕРКА ТАБЛИЦ
    // =====================================================================

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
            LogSql(sql, command, $"TableExists({tableName})");

            var sw = Stopwatch.StartNew();
            var result = command.ExecuteScalar();
            sw.Stop();

            LogSqlResult($"TableExists({tableName})", 1, sw.ElapsedMilliseconds);
            return Convert.ToInt32(result) > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка при проверке таблицы {TableName}", tableName);
            return false;
        }
    }

    public bool CheckRequiredTables(ILogger logger)
    {
        logger.LogInformation($"Попытка подключения к БД. Строка подключения: {MaskConnectionString(_connectionString)}");

        using var con = CreateConnection();
        try
        {
            con.Open();
            logger.LogInformation("Подключение к базе данных успешно установлено.");

            string[] requiredTables = { "DEVICE", "BAS_PARAM", "CARDINDEV", "CARD", "CARDIDX" };

            logger.LogInformation($"Начинаю проверку обязательных таблиц: {string.Join(", ", requiredTables)}");

            var missingTables = new List<string>();

            foreach (var table in requiredTables)
            {
                bool exists = TableExists(table);
                logger.LogDebug($"Таблица '{table}': {(exists ? "НАЙДЕНА" : "ОТСУТСТВУЕТ")}");
                if (!exists)
                {
                    missingTables.Add(table);
                }
            }

            if (missingTables.Any())
            {
                logger.LogCritical($"КРИТИЧЕСКАЯ ОШИБКА: В базе данных отсутствуют обязательные таблицы: {string.Join(", ", missingTables)}");
                return false;
            }

            logger.LogInformation("Проверка обязательных таблиц завершена успешно. Все таблицы найдены.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError($"ОШИБКА ПОДКЛЮЧЕНИЯ К БАЗЕ ДАННЫХ: {ex.Message}");
            return false;
        }
    }

    // =====================================================================
    // РАБОТА С BAS_PARAM
    // =====================================================================

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
            LogSql(deleteSql, deleteCommand, "InsertAbout/delete");

            var sw = Stopwatch.StartNew();
            int delRows = deleteCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("InsertAbout/delete", delRows, sw.ElapsedMilliseconds);

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, STRVALUE) 
                                VALUES (@id_dev, 'ABOUT', @strvalue)";

            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@strvalue", strValue.ToString());
            LogSql(insertSql, insertCommand, "InsertAbout/insert");

            sw.Restart();
            int insRows = insertCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("InsertAbout/insert", insRows, sw.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting about for id_dev={IdDev}", id_dev);
            return false;
        }
    }

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
            LogSql(deleteSql, deleteCommand, "SetOnlineState/delete");

            var sw = Stopwatch.StartNew();
            int delRows = deleteCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("SetOnlineState/delete", delRows, sw.ElapsedMilliseconds);

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE) 
                                VALUES (@id_dev, 'ONLINE', @state)";
            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@state", state);
            LogSql(insertSql, insertCommand, "SetOnlineState/insert");

            sw.Restart();
            int insRows = insertCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("SetOnlineState/insert", insRows, sw.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting ONLINE state for id_dev={IdDev}", id_dev);
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
            LogSql(deleteSql, deleteCommand, "SetLastEventID/delete");

            var sw = Stopwatch.StartNew();
            int delRows = deleteCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("SetLastEventID/delete", delRows, sw.ElapsedMilliseconds);

            string insertSql = @"INSERT INTO BAS_PARAM (ID_DEV, PARAM, STRVALUE) 
                                VALUES (@id_dev, 'LASTEVENT', @eventId)";

            using var insertCommand = new FbCommand(insertSql, con);
            insertCommand.Parameters.AddWithValue("@id_dev", id_dev);
            insertCommand.Parameters.AddWithValue("@eventId", eventId.ToString());
            LogSql(insertSql, insertCommand, "SetLastEventID/insert");

            sw.Restart();
            int insRows = insertCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("SetLastEventID/insert", insRows, sw.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting last event ID for id_dev={IdDev}", id_dev);
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
            LogSql(sql, command, "GetLastEventID");

            var sw = Stopwatch.StartNew();
            var result = command.ExecuteScalar();
            sw.Stop();
            LogSqlResult("GetLastEventID", result != null ? 1 : 0, sw.ElapsedMilliseconds);

            if (result != null && long.TryParse(result.ToString(), out long lastEventId))
            {
                return lastEventId;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting last event ID for id_dev={IdDev}", id_dev);
            return 0;
        }
    }

    // =====================================================================
    // ВСТАВКА СОБЫТИЙ
    // =====================================================================

    public int? EventInsert(
        int id_db = 1,
        int? id_eventtype = null,
        int? id_cntrl = null,
        int? id_reader = null,
        string? note = null,
        DateTime? time = null,
        int? id_video = null,
        int? id_user = null,
        int? ess1 = null,
        int? ess2 = null,
        int? idsource = null,
        long? idserverts = null)
    {
        // Лимит поля note в таблице/процедуре DEVICEEVENTS_INSERT.
        // Проверьте фактическую длину в БД через RDB$FIELDS/RDB$PROCEDURE_PARAMETERS.
        const int MAX_NOTE_LEN = 100;

        if (note != null && note.Length > MAX_NOTE_LEN)
        {
            _logger?.LogWarning(
                "EventInsert: note truncated from {Original} to {Max} chars. id_cntrl={IdCntrl}, id_eventtype={IdEventType}",
                note.Length, MAX_NOTE_LEN, id_cntrl ?? 0, id_eventtype ?? 0);

            note = note.Substring(0, MAX_NOTE_LEN);
        }


        using var con = CreateConnection();

        try
        {
            con.Open();

            const string sql = @"EXECUTE PROCEDURE DEVICEEVENTS_INSERT(
            @id_db, @id_eventtype, @id_cntrl, @id_reader, @note, @time, 
            @id_video, @id_user, @ess1, @ess2, @idsource, @idserverts)";

            using var command = new FbCommand(sql, con);
            command.CommandTimeout = 30;

            command.Parameters.AddWithValue("@id_db", id_db);
            command.Parameters.AddWithValue("@id_eventtype", id_eventtype ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_cntrl", id_cntrl ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_reader", id_reader ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@note", note ?? (object)DBNull.Value);

            string timeString;
            if (time.HasValue)
            {
                timeString = time.Value.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                timeString = "NOW";
            }
            command.Parameters.AddWithValue("@time", timeString);

            command.Parameters.AddWithValue("@id_video", id_video ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@id_user", id_user ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess1", ess1 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ess2", ess2 ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@idsource", idsource ?? 1);

            object serverTsValue = DBNull.Value;
            if (idserverts.HasValue && idserverts.Value > 0)
            {
                long seconds = idserverts.Value / 1000;//иначе не умещается в базу данных
                if (seconds > 0 && seconds <= int.MaxValue)
                {
                    serverTsValue = (int)seconds;
                }
            }
            command.Parameters.AddWithValue("@idserverts", serverTsValue);

            var outputParam = new FbParameter("@return_value", FbDbType.Integer)
            {
                Direction = ParameterDirection.ReturnValue
            };
            command.Parameters.Add(outputParam);

            LogSql(sql, command, "EventInsert");

            var sw = Stopwatch.StartNew();
            int rowsAffected = command.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("EventInsert", rowsAffected, sw.ElapsedMilliseconds);

            if (outputParam.Value != DBNull.Value)
            {
                int result = Convert.ToInt32(outputParam.Value);

                _logger?.LogDebug(
                    "784 Event inserted successfully. ID: {EventId}, Device: {DeviceId}, Type: {EventType}, timestam: {idserverts}",
                    result, id_cntrl ?? 0, id_eventtype ?? 0, idserverts ?? 0);

                return result;
            }

            _logger?.LogWarning("Event inserted but no return value received");
            return null;
        }
        catch (FbException fbEx)
        {
            _logger?.LogError(fbEx,
                "Firebird error inserting event. Device: {DeviceId}, EventType: {EventType}, ErrorCode: {ErrorCode}",
                id_cntrl ?? 0, id_eventtype ?? 0, fbEx.ErrorCode);

            if (fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("timeout"))
            {
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
            _logger?.LogError(ex,
                "Unexpected error inserting event. Device: {DeviceId}, EventType: {EventType}",
                id_cntrl ?? 0, id_eventtype ?? 0);
            return null;
        }
        finally
        {
            if (con.State != ConnectionState.Closed && con.State != ConnectionState.Broken)
            {
                con.Close();
            }
        }
    }

    // =====================================================================
    // DEVICE / CARD
    // =====================================================================

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
        LogSql(sql, getcomand, "GetDevice");

        var sw = Stopwatch.StartNew();
        var reader = getcomand.ExecuteReader();
        DataTable table = new DataTable();
        table.Load(reader);
        sw.Stop();

        LogSqlResult("GetDevice", table.Rows.Count, sw.ElapsedMilliseconds);
        return table;
    }

    public DataTable GetCardForLoad(int id_dev)
    {
        using var con = CreateConnection();
        con.Open();

        const string sql = @"select cd.id_cardindev, cd.id_card, cd.id_dev, cd.operation from cardindev cd
        join device d on d.id_dev=cd.id_dev
        join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader is null
        where d2.id_dev=@id_dev";

        using var getcomand = new FbCommand(sql, con);
        getcomand.Parameters.AddWithValue("@id_dev", id_dev);
        LogSql(sql, getcomand, "GetCardForLoad");

        var sw = Stopwatch.StartNew();
        var reader = getcomand.ExecuteReader();
        DataTable table = new DataTable();
        table.Load(reader);
        sw.Stop();

        LogSqlResult("GetCardForLoad", table.Rows.Count, sw.ElapsedMilliseconds);
        return table;
    }

    public void DeleteCardInDev(int id_cardindev)
    {
        using var con = CreateConnection();
        con.Open();

        const string sql = @"delete from cardindev cd where cd.id_cardindev = @id_cardindev";
        using var getcomand = new FbCommand(sql, con);
        getcomand.Parameters.AddWithValue("@id_cardindev", id_cardindev);
        LogSql(sql, getcomand, "DeleteCardInDev");

        var sw = Stopwatch.StartNew();
        int rows = getcomand.ExecuteNonQuery();
        sw.Stop();
        LogSqlResult("DeleteCardInDev", rows, sw.ElapsedMilliseconds);
    }

    public void UpdateCardInDevIncrement(int id_cardindev)
    {
        using var con = CreateConnection();
        con.Open();

        const string sql = @"update cardindev cd set cd.attempts = cd.attempts + 1 where cd.id_cardindev = @id_cardindev";
        using var getcomand = new FbCommand(sql, con);
        getcomand.Parameters.AddWithValue("@id_cardindev", id_cardindev);
        LogSql(sql, getcomand, "UpdateCardInDevIncrement");

        var sw = Stopwatch.StartNew();
        int rows = getcomand.ExecuteNonQuery();
        sw.Stop();
        LogSqlResult("UpdateCardInDevIncrement", rows, sw.ElapsedMilliseconds);
    }

    public int FixCardIdxOK(string idCard, int idDev, int uid)
    {
        using var con = CreateConnection();
        con.Open();

        try
        {
            string updateSql = @"UPDATE CARDIDX SET
                DEVIDX = @uid,
                LOAD_TIME = CURRENT_TIMESTAMP,
                LOAD_RESULT = 'OK'
                WHERE (ID_CARD = @idCard) AND (ID_DEV = @idDev)";

            using var updateCommand = new FbCommand(updateSql, con);
            updateCommand.Parameters.AddWithValue("@uid", uid);
            updateCommand.Parameters.AddWithValue("@idCard", idCard);
            updateCommand.Parameters.AddWithValue("@idDev", idDev);
            LogSql(updateSql, updateCommand, "FixCardIdxOK/update");

            var sw = Stopwatch.StartNew();
            int rowsUpdated = updateCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("FixCardIdxOK/update", rowsUpdated, sw.ElapsedMilliseconds);

            if (rowsUpdated == 0)
            {
                string insertSql = @"INSERT INTO CARDIDX 
                    (ID_CARD, ID_DEV, DEVIDX, LOAD_TIME, LOAD_RESULT) 
                    VALUES (@idCard, @idDev, @uid, CURRENT_TIMESTAMP, 'OK')";

                using var insertCommand = new FbCommand(insertSql, con);
                insertCommand.Parameters.AddWithValue("@uid", uid);
                insertCommand.Parameters.AddWithValue("@idCard", idCard);
                insertCommand.Parameters.AddWithValue("@idDev", idDev);
                LogSql(insertSql, insertCommand, "FixCardIdxOK/insert");

                sw.Restart();
                rowsUpdated = insertCommand.ExecuteNonQuery();
                sw.Stop();
                LogSqlResult("FixCardIdxOK/insert", rowsUpdated, sw.ElapsedMilliseconds);
            }

            return rowsUpdated;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in FixCardIdxOK for card={IdCard} id_dev={IdDev}", idCard, idDev);
            return 0;
        }
    }

    public int FixDeviceErr(string messErr, int idDev)
    {
        using var con = CreateConnection();
        con.Open();

        try
        {
            string updateSql = @"UPDATE CARDIDX cdx SET
                LOAD_TIME = CURRENT_TIMESTAMP,
                LOAD_RESULT = @messErr
                WHERE (LOAD_RESULT not containing 'OK')
                AND (ID_DEV in(
                select d2.id_dev from device d
                join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader in (0,1)
                where d.id_dev=@idDev
                 ))";

            _logger?.LogDebug("FixDeviceErr id_dev={idDev} messErr={messErr}", idDev, messErr);

            using var updateCommand = new FbCommand(updateSql, con);
            updateCommand.Parameters.AddWithValue("@messErr", messErr);
            updateCommand.Parameters.AddWithValue("@idDev", idDev);
            LogSql(updateSql, updateCommand, "FixDeviceErr");

            var sw = Stopwatch.StartNew();
            int rowsUpdated = updateCommand.ExecuteNonQuery();
            sw.Stop();
            LogSqlResult("FixDeviceErr", rowsUpdated, sw.ElapsedMilliseconds);

            return rowsUpdated;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in FixDeviceErr for id_dev={idDev}", idDev);
            return 0;
        }
    }
}