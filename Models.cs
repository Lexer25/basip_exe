using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Basip  // Используем ваш текущий namespace, не DeviceLibrary
{
    public class LogsResponse
    {
        [JsonPropertyName("list_items")]
        public List<LogItem>? list_items { get; set; }
    }

    public class LogItem
    {
        [JsonPropertyName("timestamp")]
        public long timestamp { get; set; }

        [JsonPropertyName("name")]
        public LogName? name { get; set; }

        [JsonPropertyName("info")]
        public LogInfo? info { get; set; }

        /// <summary>Ключи, под которыми панели отдают номер идентификатора.</summary>
        private static readonly string[] IdentifierKeys = { "number", "card", "code", "owner" };

        /// <summary>
        /// Номер идентификатора (карты) из события. Панели с API 2.x кладут его в info.model
        /// (info.model.number), панели с API 1.x — прямо в info (info.number). Значение может
        /// прийти и строкой, и числом, поэтому приводится через ToString().
        /// </summary>
        public string? IdentifierNumber
        {
            get
            {
                // API 2.x: info.model.number
                string? fromModel = FindIdentifier(info?.model);
                if (fromModel != null)
                {
                    return fromModel;
                }

                // API 1.x: info.number
                if (info?.fields != null)
                {
                    foreach (string key in IdentifierKeys)
                    {
                        if (info.fields.TryGetValue(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
                        {
                            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }

                return null;
            }
        }

        private static string? FindIdentifier(Dictionary<string, object>? model)
        {
            if (model == null)
            {
                return null;
            }

            foreach (string key in IdentifierKeys)
            {
                if (model.TryGetValue(key, out object? value) && value != null)
                {
                    string? text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }

        // Удобное свойство для получения DateTime из timestamp
        public DateTime EventTime => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
    }

    public class LogName
    {
        [JsonPropertyName("key")]
        public string? key { get; set; }
    }

    public class LogInfo
    {
        [JsonPropertyName("model")]
        public Dictionary<string, object>? model { get; set; }

        /// <summary>
        /// Поля, которые панели с API 1.x кладут прямо в info: number, code, card, type, owner,
        /// lock, apartment_address, apartment_name, localized (у API 2.x эти же данные лежат
        /// внутри info.model). Принимаются как есть, без привязки к типам, чтобы разбор ответа
        /// не падал на неожиданных значениях.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? fields { get; set; }
    }

    // Добавим также модель для ответа при авторизации, если её нет
    public class AuthResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    // Модель для информации об устройстве
    public class DeviceInfoResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("firmware_version")]
        public string? FirmwareVersion { get; set; }

        [JsonPropertyName("hardware_version")]
        public string? HardwareVersion { get; set; }

        [JsonPropertyName("mac_address")]
        public string? MacAddress { get; set; }
    }
}
