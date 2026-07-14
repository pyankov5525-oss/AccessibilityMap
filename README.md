# ♿ Карта доступности (AccessibilityMap)

Веб-приложение на **Blazor WebAssembly (hosted, .NET 8)** для оценки доступности
объектов городской среды (вход, ширина дверей, пути, санитарная зона, информация,
парковка, персонал) с отображением на **Яндекс.Картах**.

Каждый объект оценивается по 7 критериям (0–3 балла, итог 0–21) и получает
уровень доступности:

| Баллы | Уровень | Цвет |
|-------|---------|------|
| ≥ 18  | Полностью доступно | 🟢 green |
| 9–17  | Частично доступно | 🟡 gold |
| < 9   | Недоступно | 🔴 red |

## Стек

- **Blazor WebAssembly** (клиент) + **ASP.NET Core** (сервер, хостинг WASM)
- **SQLite** через **Entity Framework Core**
- **Яндекс.Карты 2.1** (JS-interop) + геокодер Яндекса
- CORS — разрешён любой origin (для локальной разработки WASM↔API)

## Возможности

- Карта с метками, раскрашенными по уровню доступности.
- Клик по карте в «режиме доступности» → форма добавления объекта
  (адрес подставляется через обратный геокодинг).
- Редактирование и удаление объектов прямо из балуна на карте.
- Поиск адреса через геокодер с перемещением карты.
- Фильтры по уровню доступности (Все / 🟢 / 🟡 / 🔴) и счётчик статистики.
- Страница **«Список»** (`/places`): таблица со всеми объектами, фильтрами,
  поиском и удалением; кнопка «Изменить» открывает форму на карте (`/?edit=<id>`).
- Сид-данные (объекты Воронежа) при первом запуске.

## Запуск

```bash
dotnet build
dotnet run --project AccessibilityMap.Server
```

Откройте `https://localhost:5001` (или порт из `launchSettings.json`).
Swagger API доступен в режиме разработки по `/swagger`.

База (`accessibility.db`, SQLite) создаётся и наполняется демо-данными автоматически.

## Структура

```
AccessibilityMap.Server.csproj   # ASP.NET Core: API + хостинг WASM
AccessibilityMap.csproj          # Blazor WebAssembly (клиент)
Controllers/PlacemarksController.cs   # CRUD + nearest + geocode + reverse-geocode
Data/AppDbContext.cs, Data/DbInitializer.cs
Models/PlacemarkModel.cs              # сущность + вычисляемые Level/TotalScore
Pages/Home.razor, Pages/Places.razor  # клиентские страницы
Pages/ScoreInput.razor               # селектор оценки 0–3
Layout/MainLayout.razor, Layout/NavMenu.razor
wwwroot/index.html                    # инициализация Яндекс.Карт (JS)
wwwroot/css/app.css                  # единая тёмная тема
```

## Известные ограничения (следующие шаги)

- **API-ключи Яндекса** (карты и геокодер) захардкожены в `wwwroot/index.html`
  и `Controllers/PlacemarksController.cs`. Стоит вынести в `appsettings.json` /
  User-Secrets и не коммитить.
- Нет аутентификации/авторизации (любой может добавлять/удалять объекты).
- Нет загрузки фотографий объектов.
- База данных — локальный SQLite (для продакшена лучше PostgreSQL).
