ИНСТРУКЦИЯ К ПРОГРАММЕ BAS-IP TOOLS SERVICE
Дата: 27.10.2025 
Выполнил: Дмитриев Сергей Витальевич

# 1. НАЗНАЧЕНИЕ И КРАТКОЕ ОПИСАНИЕ РАБОТЫ ПРОГРАММЫ

basip keys services - это Windows-сервис для интеграции СКУД системы с панелями управления Bas-IP.

Текущая версия приложения: 2.0.3.4 (задаётся в Basip.csproj и попадает в журнал строкой
"Версия приложения basip: ..."). Версия установщика совпадает с версией приложения.

Основные возможности:
- Синхронизация карт доступа - запись и удаление карт на панелях Bas-IP
- Обработка событий доступа - сбор событий с панелей (проход по карте, доступ запрещен)
- Автоматическое обновление - периодическая синхронизация данных
- Логирование операций - детальное протоколирование всех действий

Поддерживаемые операции с картами:
- Запись карт - добавление новых карт доступа на панели
- Удаление карт - удаление карт с панелей
- Проверка статуса - верификация существующих карт

Обрабатываемые события:
- access_granted_by_valid_identifier - доступ разрешен (код 50)
- access_denied_by_unknown_card - доступ запрещен (код 46)

Поддерживаемые версии API панелей: 1.x и 2.x. Панели с API 3.0 и выше не обслуживаются -
служба пишет предупреждение в журнал и пропускает такую панель.


# 2. УСТАНОВКА

Требования к системе:
- ОС: Windows 7/8/10/11 или Windows Server
- .NET: Runtime 8.0 или выше (целевая платформа проекта - net8.0)
- База данных: Firebird SQL
- Сеть: Доступ к панелям Bas-IP по HTTP

Процесс установки:

1. Подготовка базы данных:
   - Убедитесь, что в базе данных существуют необходимые таблицы:
     * DEVICE
     * BAS_PARAM
     * CARDINDEV
     * CARD
     * CARDIDX
   - Используется хранимая процедура DEVICEEVENTS_INSERT

2. Настройка конфигурации:
   - Отредактируйте файл appsettings.json
   - ОБЯЗАТЕЛЬНО заполните параметр Service:db_config (строка подключения к БД).
     В репозитории он пуст: строка подключения и пароль не хранятся в системе контроля версий.
   - Укажите остальные параметры работы службы

3. Сборка и установка (рекомендуемый способ):
   Запустите build.bat - он восстановит пакеты, опубликует приложение
   в publish\win-x64 и соберёт установщик Output\BasipKeyServiceSetup_v2.0.3.4.exe.
   Установщик сам регистрирует службу с именем "Basip Key Service".

   Результат сборки - один файл publish\win-x64\basipkeysservices.exe размером ~5 МБ.
   На целевой машине должен быть установлен .NET Runtime 8.0 (Microsoft.NETCore.App 8.x),
   иначе exe не запустится. Рядом с exe обязателен appsettings.json.

   Если нужен файл, не зависящий от установленного рантайма, соберите самодостаточный
   вариант (34 МБ со сжатием, 67 МБ без):
   dotnet publish Basip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\win-x64

   Публикация через профиль (тот же результат, что у build.bat):
   dotnet publish Basip.csproj -p:PublishProfile=SingleFile

   Альтернатива - вручную:
   # Публикация приложения
   dotnet publish -c Release -o "C:\Program Files\Basip Key Service"

   # Установка службы
   sc create "Basip Key Service" binPath="C:\Program Files\Basip Key Service\basipkeysservices.exe"
   sc description "Basip Key Service" "Bas-IP Integration Service"

4. Запуск службы:
   sc start "Basip Key Service"


# 3. НАСТРОЙКА

Конфигурационный файл appsettings.json:

{
  "Logging": {
    "LogLevel": {
      "Default": "Trace"
    }
  },
  "Log": {
    "LogFolerPath": "C:\\ProgramData\\Basip\\",
    "LogLevel": "Debug",
    "retainedFileCountLimit": 7
  },
  "Service": {
    "db_config": "User=SYSDBA;Password=<пароль>;Database=C:\\Path\\To\\Database.GDB;DataSource=127.0.0.1;Port=3050;",
    "format_card_uid": 2,
    "run_now": true,
    "timestart": "9:10:0",
    "timeout": "0:0:10",
    "time_wait_http": 10,
    "clear_log": true
  }
}

Параметры настройки:

Раздел "Service":
- db_config - строка подключения к Firebird базе данных (в репозитории не хранится)
- format_card_uid - формат идентификатора карты:
  * 0 - HEX 8 byte (00124CD8)
  * 2 - DEC 10 digit (0001493650)
- run_now - запуск сразу после старта (true/false)
- timestart - время первого запуска (ЧЧ:ММ:СС). Если run_now=false и текущее время
  меньше timestart, служба ждёт до этого времени; если больше - до этого времени следующих суток
- timeout - интервал между опросами (ЧЧ:ММ:СС)
- time_wait_http - таймаут HTTP запросов (секунды)
- clear_log - очистка журнала событий на панелях после обработки

Раздел "Log":
- LogFolerPath - путь к каталогу логов (имя параметра оставлено как в коде)
- LogLevel - уровень детализации файлового журнала
- retainedFileCountLimit - количество хранимых файлов логов

Раздел "Logging":
- LogLevel:Default - уровень вывода в консоль

Где хранить строку подключения:
- На стенде заказчика: прямо в appsettings.json рядом со службой.
- При локальной разработке: в appsettings.Development.json (файл в .gitignore,
  в репозиторий не попадает). Образец - appsettings.Development.json.example.

Настройка устройств в базе данных:

Устройства настраиваются через таблицы БД:

1. Таблица DEVICE - основные параметры устройств
2. Таблица BAS_PARAM - дополнительные параметры:
   - IP - IP-адрес панели (integer)
   - LOGIN - логин для авторизации
   - PASS - пароль для авторизации
   - LASTEVENT - временная метка последнего обработанного события
   - ABOUT - строка с моделью, версиями ПО и результатом авторизации
   - ONLINE - признак связи с панелью: 1 - есть связь, 0 - нет


# 4. КОНТРОЛЬ РАБОТЫ ПРИЛОЖЕНИЯ (ПОЯСНЕНИЯ К ЛОГАМ)

Уровни логирования:

- Trace - максимальная детализация, отладочная информация
- Debug - отладочная информация о процессах
- Information - информационные сообщения о работе
- Warning - предупреждения о нештатных ситуациях
- Error - ошибки выполнения операций
- Critical - критические ошибки, требующие вмешательства

Типовые сообщения в логах:

Старт/остановка службы:
32 basip start: 09:10:00 deltasleep: 00:00:00
33 Service basip write and delete card started
49 basip stop

Работа с устройствами:
71 Зарегистрировано панелей bas-ip: 5 шт.
Processing 3 devices with valid IP addresses

Операции с картами:
Command destination: writekey id_dev=1 BASE_URL http://192.168.1.100:80 key="0001234567" AddCard
Answer destination: writekey id_dev=1 BASE_URL http://192.168.1.100:80 Answer: OK key="0001234567" uid=1673

Обработка событий:
Event saved: Dev=1, Type=access_granted_by_valid_identifier, Code=50
Event saved: Dev=1, Type=access_denied_by_unknown_card, Code=46

Ошибки и предупреждения:
Skipping device ID 5 - no IP address
Device http://192.168.1.101:80 offline or auth failed
Failed to get events. Status: Unauthorized

Мониторинг работы:

1. Проверка подключения к БД:
   - Успешное подключение: Ok connect database
   - Ошибка: No connect database: [connection_string]

2. Статус устройств:
   - Онлайн: Device [ip] online and authenticated
   - Офлайн: Device [ip] offline or auth failed

3. Обработка событий:
   - Новые события: Retrieved [count] events from device [ip]
   - Нет событий: No new events to process

4. Очистка логов:
   - Успешно: Successful attempt to clear the event log
   - Ошибка: Failed attempt to clear the event log

Файлы логов:
- Расположение: C:\ProgramData\Basip\ (или указанный в настройках путь)
- Имя файла: ArtonitBasIpTools.log
- Ротация: сохраняются последние 7 файлов
- Формат: ДД-ММ-ГГГГ ЧЧ:мм:сс.fff - [Уровень] Сообщение

Диагностика проблем:

1. Нет подключения к панелям:
   - Проверить сетевую доступность
   - Проверить правильность IP-адресов в БД
   - Проверить логин/пароль авторизации

2. Карты не записываются:
   - Проверить формат format_card_uid
   - Проверить наличие карты в таблице CARDINDEV
   - Проверить HTTP-статусы в логах

3. События не обрабатываются:
   - Проверить параметр clear_log
   - Проверить временные метки в BAS_PARAM (LASTEVENT)
   - Проверить коды событий в логах панелей
   - Помнить: панели с API 3.0 и выше не обслуживаются
