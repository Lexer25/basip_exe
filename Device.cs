using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Basip
{
    public class Device
    {
        /// <summary>
        /// Категория журнала для отладочного вывода Device (логгер создаёт Worker).
        /// </summary>
        public const string DebugLogCategory = "Basip.DebugOutput";

        private readonly ILogger logger;

        public int id_dev;
        public int ctrl;
        public IPAddress? ip;
        public string? name;
        public string? base_url;
        public string login = string.Empty;
        public string password = string.Empty;
        public string? token;
        public bool is_online; // признак связи: true - усптройство на связи, false - прибор не отвечает
        public string base_url_api = string.Empty;
        public int time_wait = 10;
        public bool is_authenticated;

        /// <summary>
        /// Мажорная версия API панели (первое число из api_version), её выставляет Worker.
        /// От неё зависит адрес журнала: 1 — "log/list" с фильтром timestamp, 2 и выше —
        /// "log/items" с параметром from.
        /// </summary>
        public int major_api_version = 2;


        // Имя параметра debugLogger, а не logger: иначе параметр затеняет поле,
        // и обращение logger.LogTrace внутри конструктора уходит в nullable-параметр.
        public Device(DataRow row, int time_wait, ILogger? debugLogger = null)
        {
            // NullLogger: если логгер не передан, вызовы logger.LogTrace остаются рабочими,
            // но ничего не пишут.
            this.logger = debugLogger ?? NullLogger.Instance;
            this.time_wait = time_wait;
            try
            {

                this.id_dev = Convert.ToInt32(row["id_dev"]);
                this.ctrl = Convert.ToInt32(row["ctrl"]);
                this.name = row["ctrl_name"].ToString() ?? string.Empty;


                // Проверяем, что IP не DBNull и не пустой
                if (row["ip"] != DBNull.Value && !string.IsNullOrWhiteSpace(row["ip"].ToString()))
                {
                    byte[] ip_byte = BitConverter.GetBytes((int)row["ip"]);
                    Array.Reverse(ip_byte);
                    ip = new IPAddress(ip_byte);
                    base_url = "http://" + ip.ToString() + ":80";
                }
                else
                {
                    // Если IP нет, просто пропускаем это устройство
                    throw new InvalidOperationException("No IP address");
                }

                // Проверяем логин и пароль на DBNull
                login = (row["login"] != DBNull.Value) ? row["login"].ToString() ?? string.Empty : string.Empty;

                string passValue = (row["pass"] != DBNull.Value) ? row["pass"].ToString() ?? string.Empty : string.Empty;
                password = CreateMD5(passValue);

            }
            catch (Exception e)
            {
                // row может быть null (и столбца может не быть): обращение к row["id_dev"]
                // внутри обработчика выбрасывало второе исключение, из-за чего сообщение
                // не попадало в журнал.
                string deviceId = "unknown";
                try
                {
                    deviceId = row?["id_dev"]?.ToString() ?? "unknown";
                }
                catch (Exception)
                {
                    // оставляем "unknown"
                }

                logger.LogTrace($"93 Error creating Device object for ID {deviceId}: {e.Message}");
                throw;
            }

            base_url_api = "/api/v1";
        }

        public Device(ILogger? debugLogger = null)
        {
            this.logger = debugLogger ?? NullLogger.Instance;
            base_url_api = "/api/v1";
        }

        //получить информацию об устройстве без авторизации.
        public async Task<JsonDocument?> GetInfo()
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url ?? string.Empty) });
            var request = new RestRequest("/api/info");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            var get = await restClient.ExecuteGetAsync(request);

            // Панель может ответить 401/404/5xx с непустым телом: раньше такой ответ
            // считался признаком связи, а JsonDocument.Parse падал на не-JSON теле.
            if (get == null || !get.IsSuccessful || string.IsNullOrWhiteSpace(get.Content))
            {
                this.is_online = false;
                return null;
            }

            try
            {
                var info = JsonDocument.Parse(get.Content);
                this.is_online = true;
                return info;
            }
            catch (JsonException)
            {
                this.is_online = false;
                return null;
            }
        }

        //попытка авторизации
        public async Task<bool> Auth()
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var request = new RestRequest($@"/login?username={login}&password={password}");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            var get = await restClient.ExecuteGetAsync(request);
            if (get == null || !get.IsSuccessful || string.IsNullOrWhiteSpace(get.Content))
            {
                this.is_online = false;
                return false;
            }
            try
            {
                token = JsonDocument.Parse(get.Content).RootElement.GetProperty("token").GetString();

            }
            catch (Exception)
            {
                this.is_online = false;
                this.is_authenticated = false;
                return false;
            }
            this.is_online = true;

            if (!string.IsNullOrEmpty(token))
            {
                this.is_authenticated = true;  // Отмечаем как авторизованный
                return true;
            }
            return false;
        }

        public async Task<RestResponse> SetRemoteAccessServerSettings(bool enabled, string url, bool customServerEnabled)
        {
            RestClient restClient = new RestClient(new RestClientOptions{ Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var settingsJson = new JsonObject()
            {
                ["enabled"] = enabled,
                ["custom_server_api_url"] = url,
                ["custom_server_enabled"] = customServerEnabled
            };

            var request = new RestRequest("access/general/remote/control/settings", Method.Post);
            request.AddBody(settingsJson);
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            return await restClient.ExecuteAsync(request);
        }

        public async Task<RestResponse> DeleteCard(string uid)
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var request = new RestRequest($@"access/identifier/item/{uid}");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            RestResponse get = await restClient.ExecuteDeleteAsync(request);
            return get;
        }

        public async Task<RestResponse> AddIdentifier(string name, string type)
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });

            // Номер карты обязан быть числом: Convert.ToInt64 здесь раньше выбрасывал
            // исключение на нечисловом значении и роняло обработку всей панели.
            if (!long.TryParse(name, out long identifierNumber))
            {
                throw new InvalidOperationException($"Card number '{name}' is not numeric and cannot be sent to the panel");
            }

            var cardjson = new JsonObject()
            {
                ["identifier_owner"] = new JsonObject()
                {
                    ["name"] = name,
                    ["type"] = "owner"
                },
                ["identifier_type"] = type,
                ["identifier_number"] = identifierNumber.ToString(),
                ["lock"] = "all"
            };
            var request = new RestRequest("access/identifier");
            request.AddBody(cardjson);  //1673
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            return await restClient.ExecutePostAsync(request);
        }

        //public async Task<RestResponse> AddCard(string card)
        //{
        //    RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
        //    var cardjson = new JsonObject()
        //    {
        //        ["identifier_owner"] = new JsonObject()
        //        {
        //            ["name"] = card,
        //            ["type"] = "owner"
        //        },
        //        ["identifier_type"] = "card",
        //        ["identifier_number"] = Convert.ToInt64(card).ToString(),
        //        ["lock"] = "all"
        //    };
        //    var request = new RestRequest("access/identifier");
        //    request.AddBody(cardjson);  //1673
        //    request.AddHeader("Accept", "application/json");
        //    request.AddHeader("Content-Type", "application/json");
        //    request.AddHeader("Authorization", "Bearer " + token);
        //    return await restClient.ExecutePostAsync(request);
        //}

        public async Task<RestResponse> GetInfoUID(int uid)
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var request = new RestRequest("access/identifier/item/" + uid);
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            RestResponse get = await restClient.ExecuteGetAsync(request);
            return get;
        }

        public async Task<RestResponse> GetInfoCard(string name, int majorVersion)
        {
            RestClient restClient = new RestClient(new RestClientOptions
            {
                Timeout = TimeSpan.FromSeconds(time_wait),
                BaseUrl = new Uri(base_url + base_url_api)
            });

            var request = new RestRequest("access/identifier/items");

            if (majorVersion >= 2)
            {
                request.AddQueryParameter("filter_field", "identifier_number");
                request.AddQueryParameter("filter_type", "equal");
                request.AddQueryParameter("filter_format", "string");
                request.AddQueryParameter("filter_value", name);
            }
            else
            {
                request.AddQueryParameter("filter", $"identifier_number eq '{name}'");
            }

            request.AddQueryParameter("page_number", "1");
            request.AddQueryParameter("limit", "20");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);

            return await restClient.ExecuteGetAsync(request);
        }

        /// <summary>
        /// Аналог getEvent() из basoop.php: получить последние события журнала.
        /// Сначала одним запросом (первая страница getLog) выясняется общее количество
        /// событий, затем запрашивается последняя страница журнала — там самые свежие события.
        /// fromTimestamp — метка времени в миллисекундах, взятая из BAS_PARAM.LASTEVENT
        /// (в PHP это $this->getLastEventID()).
        /// </summary>
        public async Task<RestResponse> GetEvents(long fromTimestamp = 0, int limit = 50)
        {
            if (limit < 1) limit = 50;

            // 1. Пробный запрос — аналог getTotalCount($this->getLog($from, 50)) в basoop.php.
            //    Сами события этой страницы не нужны: из ответа берётся только количество записей.
            RestResponse probeResponse = await GetLog(fromTimestamp, limit, 1, 1);//последний параметр 1 - данные берутся для подсчета количества записей

            if (probeResponse == null)
            {
                // Панель не ответила: повторяем запрос, чтобы вернуть вызывающему коду
                // штатный ответ устройства, а не null.
                return await GetLog(fromTimestamp, limit, 1);
            }

            if (!probeResponse.IsSuccessful)
            {
                logger.LogTrace($"317 GetEvents: не удалось получить количество событий, status={probeResponse.StatusCode}, ответ возвращён без разбора страниц");
                return probeResponse;
            }

            int totalEventCount = GetTotalCount(probeResponse);

            // 2. Последняя страница журнала: ceil(total_items / limit), как в basoop.php
            //    (там это page = ceil($totalEventCount / $this->limit)).
            long pageCount = (long)Math.Ceiling((double)totalEventCount / limit);
            if (pageCount < 1) pageCount = 1;

           // logger.LogTrace($"328 GetEvents: id_dev={id_dev} base_url={base_url}  from={fromTimestamp} количество событий={totalEventCount}, количество страниц={pageCount}");
            // Метка времени в журнале — в читаемом виде: в запрос к панели уходят
            // миллисекунды, а в строке удобнее видеть дату и время.
            string fromText = fromTimestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(fromTimestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : "не задана";

            logger.LogTrace($"334 GetEvents: id_dev={id_dev} base_url={base_url} начиная с даты {fromText} (unix millis {fromTimestamp}) количество событий={totalEventCount}, количество страниц={pageCount}");

            // Если все события укладываются в одну страницу, нужная страница уже получена
            // пробным запросом — второй раз дёргать панель незачем (в basoop.php здесь
            // всегда идёт повторный getLog).
            if (pageCount == 1)
            {
                return probeResponse;
            }

            return await GetLog(fromTimestamp, limit, (int)pageCount);
        }

        /// <summary>
        /// Аналог getTotalCount() из basoop.php: общее количество записей в журнале
        /// из res.list_option.pagination.total_items. Если поле отсутствует или ответ
        /// не является JSON, возвращается 0.
        /// </summary>
        public static int GetTotalCount(RestResponse? response)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                return 0;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(response.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("list_option", out JsonElement listOption) ||
                    !listOption.TryGetProperty("pagination", out JsonElement pagination) ||
                    !pagination.TryGetProperty("total_items", out JsonElement totalItems))
                {
                    return 0;
                }

                return totalItems.ValueKind switch
                {
                    JsonValueKind.Number => totalItems.TryGetInt32(out int number) ? number : 0,
                    JsonValueKind.String => int.TryParse(totalItems.GetString(), out int parsed) ? parsed : 0,
                    _ => 0
                };
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Аналог getLog() из basoop.php: чтение журнала событий устройства постранично.
        /// Параметры запроса: locale, from (метка времени в мс), limit, page_number, sort_type.
        /// В ответе панели status — это StatusCode, а res из PHP — это Content (JSON
        /// с list_option.pagination и list_items), который разбирает вызывающий код.
        /// </summary>
        public async Task<RestResponse> GetLog(long fromTimestamp = 0, int limit = 50, int page = 1, int dataForCount=0)
        {
            logger.LogTrace($"392 GetLog start: fromTimestamp={fromTimestamp}, limit={limit}, page={page},dataForCount={dataForCount}");
            if (limit < 1) limit = 50;
            if (page < 1) page = 1;

            RestClient restClient = new RestClient(new RestClientOptions
            {
                Timeout = TimeSpan.FromSeconds(time_wait),
                BaseUrl = new Uri(base_url + base_url_api)
            });

            // Адрес журнала зависит от мажорной версии API панели:
            // 2.x и выше — "log/items" (метка времени в параметре from);
            // 1.x — "log/list" (метка времени в фильтре filter=timestamp ge <мс>).
            bool oldApi = major_api_version <= 1;
            var request = new RestRequest(oldApi ? "log/list" : "log/items");

            // Добавляем параметры запроса
            request.AddQueryParameter("locale", "ru");
            request.AddQueryParameter("limit", limit.ToString());
            request.AddQueryParameter("page_number", page.ToString());

            // ПРАВИЛЬНЫЙ ФОРМАТ ФИЛЬТРА для Bas-IP API

            if (fromTimestamp > 0)
            {
                // ВАЖНО: панель ожидает метку в миллисекундах, ровно в том виде,
                // в каком она приходит в list_items[].timestamp и хранится в BAS_PARAM.LASTEVENT.
                // Делить на 1000 нельзя: панель тогда вернёт события с начала эпохи.
                if (oldApi)
                {
                    // Панели API 1.x фильтруют по метке времени. Пробелы в фильтре должны
                    // уходить как "+", поэтому строка собирается уже в нужном виде,
                    // а повторное кодирование значения отключено.
                    request.AddQueryParameter("filter", $"timestamp+ge+{fromTimestamp}", false);
                }
                else
                {
                    request.AddQueryParameter("from", fromTimestamp.ToString());
                }
            }
            //long unixSeconds = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10800) * 1000;
            //request.AddQueryParameter("to", unixSeconds.ToString());
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");

            if (!string.IsNullOrEmpty(token))
            {
                request.AddHeader("Authorization", "Bearer " + token);
            }


            // ДЕТАЛЬНЫЙ ВЫВОД
            var sb = new StringBuilder();
            sb.AppendLine("=== REQUEST DETAILS ===");
            sb.AppendLine($"Method: GET");
            sb.AppendLine($"Base URL: {restClient.Options.BaseUrl}");
            sb.AppendLine($"Resource: {request.Resource}");
            sb.AppendLine($"Full URL: {restClient.Options.BaseUrl}/{request.Resource}");

            sb.AppendLine("\n--- Parameters ---");
            foreach (var param in request.Parameters)
            {
                sb.AppendLine($"  {param.Type}: {param.Name} = {param.Value}");
            }

            sb.AppendLine("\n--- Headers ---");
            foreach (var header in request.Parameters.Where(p => p.Type == ParameterType.HttpHeader))
            {
                sb.AppendLine($"  {header.Name}: {header.Value}");
            }

            sb.AppendLine("====================");
            logger.LogTrace($"443 {sb}");

            RestResponse response = await restClient.ExecuteGetAsync(request);

            // Результат запроса фиксируется в журнале: статус и тело ответа панели.
            if(dataForCount==0) logger.LogTrace($"455 GetLog result: status={response.StatusCode}, body={response.Content}");

            return response;
        }

        public async Task<RestResponse> SystemClearingLog()
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var request = new RestRequest($@"/system/debug/log");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            RestResponse get = await restClient.ExecuteDeleteAsync(request);
            return get;
        }

        /**27.10.2025 Функция очистки журнала событий**/
        public async Task<RestResponse> ClearingLog(int majorVersion)
        {
            RestClient restClient = new RestClient(new RestClientOptions
            {
                Timeout = TimeSpan.FromSeconds(time_wait),
                BaseUrl = new Uri(base_url + base_url_api)
            });

            RestRequest request = new RestRequest("system/data/clear", Method.Post);

            JsonObject data;

            if (majorVersion == 1)
            {
                // Для API 1.x - используем data_types
                data = new JsonObject
                {
                    ["data_types"] = new JsonArray("logs")
                };
            }
            else
            {
                // Для API 2.x - используем clear_fields
                data = new JsonObject
                {
                    ["clear_fields"] = new JsonArray("logs")
                };
            }

            request.AddStringBody(data.ToJsonString(), "application/json");
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);

            return await restClient.ExecutePostAsync(request);
        }

        public async Task<bool> Logout()
        {
            try
            {
                // Создаем клиента для API v1
                RestClient restClient = new RestClient(new RestClientOptions
                {
                    Timeout = TimeSpan.FromSeconds(time_wait),
                    BaseUrl = new Uri(base_url + base_url_api)  // http://192.168.1.100:8888/api/v1
                });

                // Создаем запрос на logout
                var request = new RestRequest("/logout", Method.Post);
                request.AddHeader("Accept", "application/json");
                request.AddHeader("Content-Type", "application/json");

                // Добавляем токен в заголовок Authorization
                if (!string.IsNullOrEmpty(token))
                {
                    request.AddHeader("Authorization", $"Bearer {token}");
                }

                // Выполняем запрос
                var response = await restClient.ExecutePostAsync(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Очищаем токен после выхода
                    token = null;
                    this.is_authenticated = false;

                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ***** НОВЫЕ МЕТОДЫ ИЗ DLL ВЕРСИИ *****

        /// <summary>
        /// Установка PIN-кода для гостя
        /// </summary>
        //public async Task<RestResponse> SetPinCode(string code)
        //{
        //    RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });

        //    var pinjson = new JsonObject()
        //    {
        //        ["identifier_owner"] = new JsonObject()
        //        {
        //            ["name"] = code,
        //            ["type"] = "guest"
        //        },
        //        ["identifier_type"] = "inputCode",
        //        ["identifier_number"] = Convert.ToInt64(code).ToString(),
        //        ["lock"] = "all"
        //    };
        //    var request = new RestRequest("access/identifier");
        //    request.AddBody(pinjson);
        //    request.AddHeader("Accept", "application/json");
        //    request.AddHeader("Content-Type", "application/json");
        //    request.AddHeader("Authorization", "Bearer " + token);
        //    return await restClient.ExecutePostAsync(request);
        //}

        /// <summary>
        /// Установка мастер-кода
        /// </summary>
        public async Task<RestResponse> SetMasterCode(string code)
        {
            RestClient restClient = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl = new Uri(base_url + base_url_api) });
            var mcodejson = new JsonObject()
            {
                ["input_code_enable"] = true,
                ["input_code_number"] = code
            };

            // ИЗМЕНЕНО: в DLL версии нет пробела в начале URL
            var request = new RestRequest("access/general/unlock/input/code");
            request.AddBody(mcodejson);
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            return await restClient.ExecutePostAsync(request);
        }

        //сделать хэш пароля
        // https://stackoverflow.com/questions/11454004/calculate-a-md5-hash-from-a-string
        public static string CreateMD5(string input)
        {
            // Use input string to calculate MD5 hash
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input ?? string.Empty);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                // ИЗМЕНЕНО: в DLL версии используется BitConverter с Replace и ToLowerInvariant
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                // Оригинальная версия для .NET 5+:
                // return Convert.ToHexString(hashBytes);
            }
        }
    }
}