# Перенос AccessibilityMap в Cloud.ru (бесплатный demo/test)

Целевая схема:

- **Cloud.ru Container Apps** — контейнер ASP.NET Core + Blazor;
- **бесплатная VM** — PostgreSQL 17 на постоянном диске;
- **Cloud.ru Object Storage** — фотографии в закрытом S3-бакете;
- **GitHub Container Registry (GHCR)** — бесплатная сборка и хранение публичного demo-образа.

> Это схема для демонстрации и тестирования без бюджета. Бесплатный лимит Container
> Apps (25 vCPU×ч и 50 ГБ RAM×ч в месяц) не обеспечивает круглосуточную работу.
> Задайте минимум реплик `0`; первый запрос после простоя будет медленным. Перед
> обработкой реальных персональных данных нужна юридическая и инфраструктурная
> проверка, резервное копирование и мониторинг.

Официальные страницы лимитов:

- VM: <https://cloud.ru/docs/evolution/overview/topics/free-tier__virtual-machines>
- Object Storage: <https://cloud.ru/docs/evolution/overview/topics/free-tier__object-storage>
- Container Apps: <https://cloud.ru/docs/evolution/overview/topics/free-tier__container-apps>

## 0. Правила безопасности

1. Не отправляйте пароли, `DATABASE_URL`, S3 secret key и JWT-ключ в чат или Git.
2. Вводите секреты только в Secret/Environment UI Cloud.ru либо локально через
   скрытый prompt/менеджер паролей.
3. Не удаляйте BLOB фотографий из старой БД: приложение использует БД как fallback.
4. Не выключайте старую площадку до восстановления backup, сверки данных и smoke-test.

## 1. Регистрация и free tier

1. Откройте <https://cloud.ru/> и создайте аккаунт физического лица/организации.
2. Подтвердите контакты и войдите в консоль **Cloud.ru Evolution**.
3. Активируйте free tier согласно мастеру консоли. Условия и названия пунктов UI
   могут меняться — до подтверждения убедитесь, что итоговая стоимость равна нулю.
4. Создайте один проект, например `accessibility-map-demo`, и одну сеть.
5. Создайте приватную подсеть. PostgreSQL VM и сетевое подключение Container Apps
   должны находиться в совместимых сети/подсети. Публичный порт 5432 не нужен.

Если консоль показывает ненулевую стоимость, остановитесь и не создавайте ресурс.

## 2. PostgreSQL на бесплатной VM

1. Создайте бесплатную VM: Ubuntu LTS, 2 vCPU, 4 ГБ RAM, системный диск 30 ГБ.
2. При создании можно передать `postgres/cloud-init.yaml` как cloud-init.
3. В группе безопасности разрешите:
   - SSH/22 только со своего административного IP (или используйте штатный доступ);
   - TCP/5432 **только из приватной подсети Container Apps**;
   - запретите 5432 из `0.0.0.0/0`.
4. Если cloud-init включил UFW, на VM разрешите подсеть приложения, например:

   ```bash
   sudo ufw allow from 10.20.0.0/24 to any port 5432 proto tcp
   ```

5. Скопируйте каталог `deploy/cloudru/postgres` в
   `/opt/accessibilitymap/postgres`, затем на VM:

   ```bash
   cd /opt/accessibilitymap/postgres
   cp .env.example .env
   printf '%s' 'СГЕНЕРИРОВАННЫЙ_ДЛИННЫЙ_ПАРОЛЬ' | sudo tee secrets/postgres_password >/dev/null
   sudo chmod 600 secrets/postgres_password
   sudo docker compose up -d
   sudo docker compose ps
   ```

Пароль хранится только на VM. Каталоги `secrets` и `backups` игнорируются Git.
Для production настройте TLS PostgreSQL; для demo допускается отключение TLS только
во внутренней сети и при закрытом публичном 5432.

## 3. Сначала backup и тест восстановления

На машине с PostgreSQL client tools задайте URL исходной базы, не вставляя его в
команду, сохраняемую в историю:

```bash
read -rsp 'SOURCE_DATABASE_URL: ' SOURCE_DATABASE_URL; echo
export SOURCE_DATABASE_URL
./deploy/cloudru/scripts/backup-and-verify.sh
unset SOURCE_DATABASE_URL
```

Скопируйте `.dump` и `.sha256` в защищённое место. На новой VM сначала создайте
**одноразовую тестовую БД**, восстановите её и сравните критические таблицы:

```bash
sudo docker exec -it <postgres-container> createdb -U accessibilitymap accessibilitymap_restore_test
export SOURCE_DATABASE_URL='...source...'
export TARGET_DATABASE_URL='postgresql://accessibilitymap:...@PRIVATE_VM_IP:5432/accessibilitymap_restore_test?sslmode=disable'
./deploy/cloudru/scripts/restore-and-compare.sh migration-backups/accessibilitymap-TIMESTAMP.dump
```

Скрипт проверяет checksum файла и количества пользователей, ролей, назначений ролей,
меток, фотографий, журнала и голосов. После успешного теста удалите test-БД, создайте
пустую целевую `accessibilitymap` и повторите восстановление туда. Скрипт намеренно
откажется писать в непустую БД.

Для финального переключения остановите запись на старом приложении, сделайте новый
финальный dump и повторите восстановление — иначе изменения между первым dump и
переключением потеряются.

## 4. Object Storage и миграция фотографий

1. Создайте **закрытый** бакет Object Storage в регионе `ru-central-1`.
2. Создайте отдельный S3 access key с доступом только к этому бакету.
3. Никогда не делайте бакет публичным: фотографии отдаются контроллером приложения
   после проверки доступа.
4. Настройки приложения:

   ```text
   S3__ServiceUrl=https://s3.cloud.ru
   S3__Region=ru-central-1
   S3__Bucket=<bucket>
   S3__AccessKey=<secret setting>
   S3__SecretKey=<secret setting>
   S3__ForcePathStyle=true
   ```

Для переноса запустите тот же образ однократно с аргументом
`--migrate-photos-to-s3`, направив `DATABASE_URL` на восстановленную новую БД.
Пример на машине с Docker (значения должны приходить из environment/secret store):

```bash
docker run --rm \
  -e DATABASE_URL -e POSTGRES_REQUIRE_SSL \
  -e S3__ServiceUrl -e S3__Region -e S3__Bucket \
  -e S3__AccessKey -e S3__SecretKey -e S3__ForcePathStyle \
  -e Jwt__Key \
  ghcr.io/OWNER/REPOSITORY:cloudru-demo --migrate-photos-to-s3
```

Команда загружает только непустые BLOB, скачивает объекты обратно и сравнивает
SHA-256. При любой ошибке завершается ненулевым кодом; BLOB в БД не удаляются.
Повторный запуск безопасен. Новые загрузки записываются в S3, а при ошибке S3
остаются в БД. Чтение сначала проверяет S3, затем старый BLOB и локальный файл.

## 5. Сборка образа без платного registry

Workflow `.github/workflows/cloudru-image.yml` собирает образ при push и вручную:

1. GitHub → **Actions** → `Build Cloud.ru container image` → **Run workflow**.
2. Дождитесь зелёного результата.
3. В GitHub Packages откройте пакет и для demo сделайте его публичным либо настройте
   в Container Apps credentials для приватного GHCR.
4. Используйте неизменяемый tag с SHA для контрольного запуска; tag
   `cloudru-demo` удобен для обновлений.

## 6. Container Apps

Создайте Container App со следующими параметрами (точные подписи полей UI могут
меняться):

- image: `ghcr.io/OWNER/REPOSITORY:<commit-sha>`;
- container port: `8080`, HTTP ingress включён;
- minimum replicas: `0`, maximum replicas: `1`;
- минимально доступные CPU/RAM;
- health endpoint: `/health`;
- подключение к приватной сети VM;
- startup timeout увеличьте для cold start Blazor/.NET.

Обычные environment variables:

```text
ASPNETCORE_ENVIRONMENT=Production
POSTGRES_REQUIRE_SSL=false
S3__ServiceUrl=https://s3.cloud.ru
S3__Region=ru-central-1
S3__ForcePathStyle=true
Jwt__Issuer=AccessibilityMap
Jwt__Audience=AccessibilityMap.Client
```

Secret environment variables:

```text
DATABASE_URL=postgresql://accessibilitymap:<URL_ENCODED_PASSWORD>@<PRIVATE_VM_IP>:5432/accessibilitymap
Jwt__Key=<random, at least 32 bytes>
S3__Bucket=<bucket>
S3__AccessKey=<access-key>
S3__SecretKey=<secret-key>
YANDEX_MAPS_API_KEY=<key, if used>
YANDEX_GEOCODER_API_KEY=<key, if used>
SEED_LOGIN=<optional recovery login>
SEED_PASSWORD=<optional recovery password>
```

`POSTGRES_REQUIRE_SSL=false` разрешён только потому, что соединение остаётся во
внутренней сети. Если настроен TLS PostgreSQL, удалите переменную или задайте `true`.
После появления URL добавьте его в `CORS_ORIGINS`, только если API вызывается с
другого origin; hosted Blazor работает same-origin без этого.

## 7. Проверка и переключение

До изменения пользовательского адреса проверьте:

- `/health` отвечает `200` после cold start;
- вход существующим Developer/Manager/Volunteer работает;
- роли и список пользователей совпадают;
- число меток и записей журнала совпадает со старой системой;
- открываются старые фотографии;
- создаётся новая метка с фото, проходит модерацию и видна гостю;
- мобильный интерфейс, размеры текста и Яндекс.Карта работают;
- после перезапуска VM и Container App данные и фото сохраняются;
- в логах нет ошибок PostgreSQL/S3.

Оставьте старую инфраструктуру доступной только для rollback и без новых записей.
После периода проверки обновите адрес/DNS. Старую систему отключайте лишь после
подтверждения backup, восстановления, SHA-256 фотографий и smoke-test. Backup не
удаляйте вместе со старой инфраструктурой.

## 8. Эксплуатационные ограничения

- 30 ГБ VM делятся между ОС, PostgreSQL, WAL и backup: контролируйте свободное место.
- Не храните единственный backup на той же VM.
- Для реальной эксплуатации нужны регулярные base backup + WAL archive, оповещения,
  обновления ОС/образов и проверка восстановления по расписанию.
- При исчерпании месячного лимита Container Apps приложение может остановиться;
  бесплатный вариант не обещает SLA или always-on.
