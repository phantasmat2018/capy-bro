<div align="center">

# 🦫 CapyBro

**AI-помічник для будь-якого тексту у будь-якій програмі — один хоткей, нуль трення.**

[![Website](https://img.shields.io/badge/website-capybro.app-2563eb?style=flat-square)](https://capybro.app)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square)](#-установка-для-користувачів)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://learn.microsoft.com/en-us/dotnet/)
[![Version](https://img.shields.io/badge/version-2.0.0-success?style=flat-square)](#-стан-проєкту)
[![Tests](https://img.shields.io/badge/tests-600%2B%20passing-brightgreen?style=flat-square)](#quality-gate)
[![Built with](https://img.shields.io/badge/built%20with-Claude%20Code-cc785c?style=flat-square)](https://docs.claude.com/en/docs/claude-code/overview)

[capybro.app](https://capybro.app) · [Скачати](https://github.com/phantasmat2018/capy-bro/releases) · [Швидкий старт](#-установка-для-користувачів) · [FAQ](#-faq--troubleshooting)

</div>

---

## 🦫 Що це

**CapyBro** — це Windows tray-утиліта, яка пускає AI у вашу повсякденну роботу з текстом без жодного зайвого кроку.

Виділили текст у Word, Chrome, VS Code, Slack, Notepad — будь-де → натиснули глобальний хоткей (`Ctrl+Shift+E` за замовчуванням) → AI переписав за вашим промтом → новий текст уже стоїть на місці виділеного. Ніяких вкладок браузера, copy-paste у ChatGPT і назад, ніяких ручних промптів щоразу.

Працює офлайн-як-можливо: один HTTPS-запит до [OpenRouter](https://openrouter.ai) (десятки моделей через єдиний акаунт — GPT-4o, Claude, Gemini, Llama, etc), решта — локально. Native .NET 8 / WPF, без браузера, без Electron. Сидить у системному треї і прокидається тільки на ваш хоткей. Розповсюджується як один self-contained `.exe` інсталятор (~49 MB).

**Free та Pro.** Цей репозиторій — безкоштовне відкрите ядро CapyBro під ліцензією MIT; усе описане в цьому README доступне безкоштовно. Окремо існує **CapyBro Pro** — бекап налаштувань, преміум-набори промптів, експорт історії, хоткей перемикання моделей і статистика використання; одноразова покупка $19 (без підписки) на [capybro.app](https://capybro.app).

> **Чому "CapyBro"?** Капібара — найхалявніша і найдружніша тварина. Вона нікуди не поспішає, всім допомагає, і ладнає з усіма. Утиліта, яка тихо сидить поруч і робить нудну роботу замість вас — саме той вайб.

---

## 📖 Зміст

- [✨ Що вміє](#-що-вміє)
- [⌨ Гарячі клавіші](#-гарячі-клавіші)
- [📥 Установка для користувачів](#-установка-для-користувачів)
- [⚙ Налаштування](#-налаштування)
- [🔐 Приватність і безпека](#-приватність-і-безпека)
- [❓ FAQ / Troubleshooting](#-faq--troubleshooting)
- [🚀 Швидкий старт для розробників](#-швидкий-старт-для-розробників)
- [🧩 Архітектура та tech stack](#-архітектура-та-tech-stack)
- [📊 Стан проєкту](#-стан-проєкту)
- [🗺 Roadmap](#-roadmap)
- [🤖 AI-driven workflow](#-ai-driven-workflow)
- [🔐 Code signing](#-code-signing)
- [📚 Корисні посилання](#-корисні-посилання)
- [🙏 Подяки](#-подяки)
- [📜 Ліцензія](#-ліцензія)

---

## ✨ Що вміє

### 🎯 Основні можливості

- **Глобальний хоткей** (`Ctrl+Shift+E` за замовчуванням):
  виділили текст у Word / Chrome / VS Code / куди завгодно → натиснули → AI переписав за вашим стандартним промтом → готовий результат уже в документі. Працює над будь-якою програмою, що підтримує copy-paste.
- **Меню промтів** (`Ctrl+Shift+Q` за замовчуванням):
  toast зі списком ваших промтів. Стрілки/Enter для вибору, Esc для скасування, цифри `1-9` як швидкий доступ. Лого + назва "CapyBro" у заголовку, popover автоматично закривається коли клік йде в інший процес (foreground-window polling).
- **Undo** (`Ctrl+Shift+Z`):
  миттєво відкочує останню заміну до оригінального тексту. Працює навіть якщо історія вимкнена — TextProcessor тримає last-original у пам'яті.
- **Кастомні промти**:
  створюйте/редагуйте свої сценарії (виправити помилки, перекласти, скоротити, формальніше, дружніше, technical-style, marketing-tone, etc). Кожен промт може мати:
  - власну модель (override default для саме цього промту);
  - флаг "зберегти мову оригіналу" (AI не перекладе UA→EN випадково);
  - власну температуру / max tokens;
  - прев'ю різниці перед застосуванням.

### 📚 Робота з результатами

- **Історія покращень** (за замовчуванням увімкнено з v14):
  зберігає оригінал / результат / промт / модель / час останніх 200 запусків. Пошук + копіювання назад / Undo з будь-якого запису. Можна вимкнути в Налаштуваннях → Додаткові функції. Файл `~/.ai_text_improver_v2_history.json`.
- **Diff preview** (опціональний модал):
  порівняння оригіналу + результату side-by-side з підсвічуванням змін. Accept / Regenerate / Reject. Зручно для довгих текстів, де "одним поглядом" не побачиш що змінилося.
- **Тостове сповіщення** з потоковим відображенням генерації (streaming) — видно як AI типує. Не блокує вашу роботу — можете продовжувати робити що завгодно, поки AI генерує. Кнопка ✕ скасовує запит миттєво.

### 🔌 Провайдер LLM — на ваш вибір

CapyBro v2.0 (config schema v20) підтримує два бекенди — переключаються однією галочкою у Налаштуваннях → Провайдер:

- **OpenRouter (хмара, за замовчуванням)** — швидко, широкий вибір моделей (GPT-4o, Claude, Gemini, Llama тощо), pay-as-you-go. Потрібен API-ключ з [openrouter.ai/keys](https://openrouter.ai/keys).
- **Ollama (локально)** — для тих, хто **категорично не хоче відправляти текст у хмару**. Запустіть [ollama](https://ollama.com), завантажте будь-яку модель (`ollama pull gemma3`), натисніть «Оновити моделі» — і весь pipeline працює офлайн. Ніяких ключів, ніякого білінгу, нічого не виходить за межі вашого комп'ютера. Стандартна адреса — `http://localhost:11434`, але ви можете перенаправити CapyBro на віддалений Ollama-сервер у вашій LAN.

**Як перемкнути на Ollama:**
1. Встановіть [Ollama](https://ollama.com) і запустіть `ollama serve` (зазвичай auto-start після інсталяції).
2. У терміналі: `ollama pull gemma3` (або будь-яку іншу модель з [бібліотеки](https://ollama.com/library)).
3. Налаштування → Провайдер → ✓ «Використовувати локальну модель (Ollama)». CapyBro робить health-check: якщо `ollama serve` не запущений, з'являється тост-попередження і галочка повертається в OpenRouter (запобігає silent-fail на гарячій клавіші).
4. Налаштування → Локальні моделі (Ollama) → натисніть кнопку оновлення (іконка зі стрілками). З'явиться список тегів які ви pull-ули. Виберіть один.
5. Готово — `Ctrl+Shift+E` працює як завжди, але через локальний backend.

**Окремо для кожного провайдера зберігається:**
- Поточна обрана модель (`Model` для OpenRouter, `OllamaModel` для Ollama)
- Список pinned моделей у відповідному dropdown
- Per-request таймаут (60s для OpenRouter, 120s для Ollama — локальні моделі стартують повільніше на cold-start)
- Per-prompt модель override (`Prompt.Model` + `Prompt.OllamaModel`)

При перемиканні OpenRouter↔Ollama жоден з цих наборів не губиться — повернувшись назад, бачите свою попередню конфігурацію.

**Візуальні індикатори активного провайдера:**
- Tooltip tray-іконки змінюється з `CapyBro` → `CapyBro · Ollama`
- Sidebar footer у вікні Налаштувань поряд з `v2.0.0 · capybro.app` показує brand-color outline-pill `Ollama`
- Header меню промтів (`Ctrl+Shift+Q`) — pill у правому верхньому куті
- Toast обробки `Обробка… · Ollama` — щоб ви бачили куди йде запит ще під час очікування

**Cost-estimator / Privacy-redaction** автоматично приховуються коли активний Ollama: для локальної моделі білінгу немає, і PII-маскування не має сенсу коли текст не покидає машину.

**Авто-детекція реального стану Ollama** (без фонового опитування):
- Секція «Провайдер» з'являється тільки коли `ollama serve` запущений (probe `GET /api/tags`); якщо не запущений — секція прихована, щоб не дражнити користувача функцією яку він не може використати.
- Probe виконується на 7 user-controlled events: старт застосунку, відкриття вікна Налаштувань (через ліво-клік або контекстне меню трея), клік по sidebar-вкладці (General/Prompts/History), згортання/розгортання вікна, maximize, та close-to-tray. Жоден фоновий таймер — мінімум зайвого трафіку до Ollama.
- **Авто-перемикання на OpenRouter коли Ollama зникає**: якщо ви були на Ollama і `ollama serve` зупинився, програма це виявляє при наступній взаємодії і автоматично перемикається назад на OpenRouter. Спершу показується червоний попереджуючий toast `Не вдалося підключитися до Ollama…`, через 2.5 секунди — зелений confirmation `Програму успішно перемкнуто на OpenRouter…`. Зміна Provider сберігається на диск, тож наступний запуск стартує чисто на OpenRouter без re-trigger toast'у.

### 🎨 UX / Зручність

- **3 мови UI**: Українська, Російська, English. Перемикається мить-в-мить, без перезапуску.
- **Темна тема** + custom WindowChrome caption (без стандартного Windows title bar).
- **Уніфіковане кольорове Cut/Copy/Paste context-menu** у всіх text-input полях.
- **Pixel-smooth scroll** у ModelsDialog / PromptPickerWindow / HistoryTab — без типового Win-stutter на колесі миші.
- **Стійкість до конфліктів буфера обміну**: якщо інший додаток (clipboard manager, RDP-канал, антивірус) тримає Win32-clipboard зайнятим, CapyBro перевідбирає його через async retry-цикл — UI не зависає на спроби, продовжує реагувати на кліки та анімації.
- **Автозапуск з Windows**: чекбокс у Налаштуваннях додає Run-key з прапором `--silent` щоб запуск не відкривав вікно.
- **Onboarding-візард** при першому запуску — 4 кроки (Welcome / API-ключ / хоткеї / Done).

### 🧪 Експериментальні функції

Усі під чекбоксами у Налаштуваннях → Додаткові функції:

- **Privacy redaction**: авто-маскування PII (email, телефони, картки, IBAN, ПІБ) перед відправкою у модель. Текст відновлюється після відповіді — модель бачить `<<EMAIL_1>>`, ви бачите `john@example.com`.
- **Cost estimator**: показує приблизну вартість запиту до OpenRouter + поточний баланс кредитів акаунта.
- **Per-prompt model**: для кожного промту окрема модель замість єдиної глобальної.
- **Налаштовуваний таймаут запиту**: за замовчуванням 60 секунд; `0` = безкінечний таймаут (для повільних reasoning-моделей або великих контекстів).
- **Keep result selected**: після paste-back залишити новий текст виділеним (зручно для подальших ітерацій). _Розблоковується через 20 тапів по око-іконці у Налаштуваннях._

---

## ⌨ Гарячі клавіші

| Дія | За замовчуванням | Налаштувати |
|---|---|---|
| Запустити default-промт на виділеному тексті | `Ctrl+Shift+E` | Налаштування → Загальне |
| Відкрити меню промтів | `Ctrl+Shift+Q` | Налаштування → Загальне |
| Відмінити останню заміну (Undo) | `Ctrl+Shift+Z` | Налаштування → Загальне |
| Скасувати поточний запит | `Esc` (на toast) або `✕` button | — |
| Toggle Settings вікно | Лівий клік на tray-іконку | — |
| Quit | Правий клік на tray → Quit | — |

Усі хоткеї реєструються через Win32 `RegisterHotKey` з `MOD_NOREPEAT`, тому працюють глобально поверх будь-якої програми. Конфлікти з системними / іншими утилітами визначаються одразу: onboarding-візард і Налаштування → Загальне підсвічують конфліктний хоткей червоним.

---

## 📥 Установка для користувачів

### Скачати

Скачайте останній реліз з [GitHub Releases](https://github.com/phantasmat2018/capy-bro/releases) — файл `CapyBro-Setup-2.0.0.exe` (~49 MB).

Або зайдіть на [capybro.app](https://capybro.app) — там завжди свіже посилання.

### Запустити інсталятор

Запустіть інсталятор → майстер у 3 кліки → готово. Іконка з'явиться у системному треї біля годинника.

Інсталятор per-user — НЕ потребує адмін-прав, ставить у `%LOCALAPPDATA%\CapyBro\`.

> **SmartScreen попередження:** інсталятор зараз непідписаний, тому Windows покаже "Невідомий видавець". Натисніть "Додатково" → "Все одно виконати". Це знаємо й плануємо підписати сертифікатом (див. [Code signing](#-code-signing) нижче).

### Перший запуск

Запустить onboarding-візард:

1. **Оберіть мову інтерфейсу** — Українська / Русский / English.
2. **Вставте API-ключ** з [openrouter.ai/keys](https://openrouter.ai/keys). Безкоштовний акаунт дає доступ до десятків моделей (включно з безкоштовними).
3. **Налаштуйте гарячу клавішу** (за замовчуванням `Ctrl+Shift+E`). Візард одразу скаже, чи цей хоткей вільний.
4. **Готово** — виділіть текст у будь-якій програмі, натисніть хоткей.

### Що далі

Tray-іконка → Settings → вкладка Промти. Створіть кілька промтів під свої сценарії: "виправ помилки", "переклади на англійську", "зроби формальнішим", "скороти до 100 слів", "поясни простіше", etc. Для кожного — окрема модель і температура за бажанням.

---

## ⚙ Налаштування

Файл: `%USERPROFILE%\.ai_text_improver_v2_config.json` (schema v20)

Більшість опцій редагуються через UI (Налаштування → Загальне). Розширені налаштування:

- **Додаткові функції** (завжди видимі):
  - `experimental_diff_preview`, `experimental_streaming`, `experimental_per_prompt_model`, `experimental_costs_and_credits`, `experimental_privacy_redaction`
  - `experimental_history` — **ON за замовчуванням** з v14
  - `Timeout` (1-N секунд, `0` = безкінечний)

- **Beta features** (тільки після 20-тапу по око-іконці у Налаштуваннях розблоковує):
  - `experimental_keep_result_selected`

### Sentinel: `Timeout = 0` означає безкінечний таймаут

З v14 значення `0` у полі Request timeout — це валідний sentinel "чекати скільки треба". `TextProcessor` перекладає `0` → `Timeout.InfiniteTimeSpan` перед передачею в `OpenRouterClient`, який пропускає `CancelAfter()` повністю. Зовнішнє скасування (user Cancel, OnExit ShutdownGracefully) все ще працює — вимикається тільки **schedule-based expiry**.

Pre-v14 JSON-файли що не мали поля `timeout` (deserialize → 0) автоматично clamping'уються до Default 60 через `ConfigVersion < 14` check у `WithDefaultsApplied`.

---

## 🔐 Приватність і безпека

CapyBro по-замовчуванню збирає ZERO телеметрії — жодних analytics, crash reporting, опитувань. Усі мережеві запити — це лише ваші запити до OpenRouter (один HTTPS-call на хоткей). Більше нічого нікуди не йде.

### Зберігання даних

| Що | Де | Чому саме там |
|---|---|---|
| API ключ | **Windows Credential Manager** (`CapyBro/OpenRouter`) | Шифрується DPAPI за поточним користувачем. Інші користувачі / інші машини не прочитають. |
| Конфіг | `~/.ai_text_improver_v2_config.json` | Plaintext JSON. Містить промти, хоткеї, налаштування — без API ключа. |
| Історія | `~/.ai_text_improver_v2_history.json` | Plaintext JSON. Можна вимкнути в налаштуваннях; тоді файл взагалі не пишеться. |
| Логи | `~/.ai_text_improver_v2.log` | Diagnostic info, БЕЗ вмісту запитів і відповідей. Перезаписується щодня. |

### Privacy redaction (експериментально)

Опція **Налаштування → Додаткові функції → Маскування PII** автоматично замінює перед відправкою у модель:

- email-адреси → `<<EMAIL_n>>`
- телефони (з різними форматами) → `<<PHONE_n>>`
- URLs → `<<URL_n>>`
- кредитні картки → `<<CARD_n>>`
- IBAN → `<<IBAN_n>>`
- ПІБ (heuristic — заголовні слова не на початку речення) → `<<NAME_n>>`

Після відповіді AI оригінальні значення підставляються назад. Модель ніколи не бачить ваших реальних PII. Implementation: `Services/PrivacyRedactor.cs` + регресійні тести у `tests/CapyBro.Tests/Services/PrivacyRedactorTests.cs`.

### Що НЕ передається

- Хоткеї, налаштування, історія — все локально.
- Логи не містять вмісту тексту / відповідей моделі.
- Жодного автообновлення, що "перевіряє" сервер.

---

## ❓ FAQ / Troubleshooting

### Хоткей не реагує

1. Перевірте, чи цей хоткей не зайнятий іншою утилітою (наприклад, Snipping Tool `Ctrl+Shift+S` конфліктує).
2. Налаштування → Загальне → блок "Гарячі клавіші". Конфлікт підсвічується червоним з підказкою.
3. Змініть на щось унікальне типу `Ctrl+Alt+Shift+E`.

### AI повертає переклад замість виправлення / навпаки

- Налаштування → Промти → відкрийте свій промт → перевірте флаг **"Зберегти мову оригіналу"**. Без нього AI може "виправити" український текст шляхом перекладу на англійську (бо так точніше — з його POV).

### Toast зник, але новий текст не з'явився

- Перевірте, чи у цільовій програмі є фокус на текстовому полі. CapyBro вставляє через clipboard + `Ctrl+V` — поле має приймати paste.
- Деякі sandbox'овані додатки (UWP / WSA) блокують keyboard automation. Workaround: вставте з clipboard вручну (Ctrl+V).

### "Невідомий видавець" попередження

Інсталятор зараз непідписаний (SmartScreen accumulation period — ~$200/рік за OV сертифікат + 2-4 тижні репутації). Сафе — натисніть "Додатково" → "Все одно виконати". Або зачекайте, поки купимо EV cert (план див. [Code signing](#-code-signing)).

### Settings вікно не відкривається

- Іконка у треї повинна бути жива (не Windows-сіра). Якщо сіра — додаток впав. Перезапустіть з Start menu.
- Лог: `%USERPROFILE%\.ai_text_improver_v2.log` — там буде stack trace.

### Як скинути все до дефолтних значень

Закрийте додаток через tray → Quit. Видаліть три файли:
- `~/.ai_text_improver_v2_config.json`
- `~/.ai_text_improver_v2_history.json`
- API ключ через Credential Manager → Windows Credentials → знайти `CapyBro/OpenRouter` → Remove

При наступному запуску знову з'явиться onboarding-візард.

### Працює на Windows 10?

Так, мінімальна вимога — Windows 10 1809 (для WPF / DPI awareness). Більшість фіч прозоро деградують на старіших білдах (Mica → solid background).

### Працює на macOS / Linux?

Ні. Залежить від Win32 API (RegisterHotKey, SendInput, Credential Manager, NotifyIcon). Порт можливий через інший stack (Avalonia + alternative tray + keyring), але це переписаний від нуля проєкт.

---

## 🚀 Швидкий старт для розробників

```powershell
# Клонувати
git clone https://github.com/phantasmat2018/capy-bro.git
cd capy-bro

# Запустити з-під dotnet (Debug)
dotnet run --project src/CapyBro

# Тести (понад 600 регресійних на момент написання)
dotnet test

# Self-contained збірка (папка) для релізу
dotnet publish src/CapyBro -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish/win-x64

# NSIS installer (потрібен makensis у PATH або у `Program Files (x86)\NSIS\Bin\`)
& "C:\Program Files (x86)\NSIS\Bin\makensis.exe" installer\installer.nsi
# → installer/CapyBro-Setup-2.0.0.exe

# (Опціонально) Підписати інсталятор Authenticode-сертифікатом
$env:CAPYBRO_SIGN_THUMBPRINT = "<SHA1-thumbprint-cert-у-CurrentUser\My>"
pwsh installer/sign-installer.ps1
```

### Quality gate

Кожен коміт має проходити три перевірки:

```powershell
dotnet format --verify-no-changes   # стиль коду
dotnet build -warnaserror           # 0 warnings (TreatWarningsAsErrors=true)
dotnet test                         # 600+ regression tests
```

Усі троє мають бути зеленими — non-negotiable.

### Локальні гачки

- **Не запускайте `dotnet build` поки `CapyBro.exe` у треї** — DLL заблокована running процесом. Перед збіркою: `taskkill /F /IM CapyBro.exe /IM testhost.exe /IM dotnet.exe`.
- **InternalsVisibleTo для тестів** — `CapyBro.Tests` має доступ до internal-методів через атрибут у csproj. Не треба reflection для тестування приватного.
- **Translator parity invariant** — будь-який новий ключ перекладу мусить бути в усіх 3 локалях (UA/RU/EN), інакше `TranslatorParityTests` падає.

---

## 🧩 Архітектура та tech stack

| Layer | Технологія | Чому |
|---|---|---|
| UI | **WPF / .NET 8** | Native Windows, без Electron-overhead. Custom WindowChrome для DARK title bar. |
| MVVM | **CommunityToolkit.Mvvm** | `[ObservableProperty]`, `[RelayCommand]` — мінімум boilerplate. |
| DI | **Microsoft.Extensions.Hosting** | Generic Host для tray-апи: lifetime, services, options, logging — все з коробки. |
| Логування | **Serilog** | Структуроване, до файлу + дебаг-вікна. |
| Гарячі клавіші | **Win32 RegisterHotKey** (P/Invoke) | Глобальні — працюють поверх будь-якої програми. |
| Tray-іконка | **H.NotifyIcon** | Сучасніший wrapper навколо WPF NotifyIcon. |
| API | **OpenRouter** (HTTPS, SSE для streaming) | Доступ до десятків AI моделей через один акаунт. |
| Single-instance | Named `Mutex` + `EventWaitHandle` | Другий запуск активує перший замість дублювання. |
| Credential store | **Windows Credential Manager** | API ключ ніколи не зберігається у JSON. |
| Configuration | JSON у `~/.ai_text_improver_v2_config.json`, schema v20 | Atomic save через tmp+File.Replace, версійований schema. |
| Diff render | **DiffPlex** | Side-by-side порівняння оригіналу + результату у Diff preview modal. |
| Іконки | **Lucide** (ISC) | 24×24 stroke-based pictograms; вшиті у `Themes/Icons.xaml`. |
| Installer | **NSIS 3.10** | Per-user install (no admin), MUI2 wizard. |

### Структура проєкту

```
capy-bro/
├── src/CapyBro/                # WPF додаток
│   ├── App.xaml(.cs)           # entry point, DI host, error handlers, OnStartup/OnExit
│   ├── Models/                 # AppConfig (schema v20), HistoryEntry, Prompt, Language, …
│   ├── Services/               # ConfigStore, HotkeyManager, OpenRouterClient,
│   │                           #   TextProcessor, HistoryStore, AutostartService,
│   │                           #   Translator, ToastPresenter, PrivacyRedactor, …
│   ├── ViewModels/             # GeneralTabVM, PromptsTabVM, HistoryVM, …
│   ├── Views/                  # XAML вікна та сторінки (PromptPickerWindow з
│   │                           #   foreground-window poller, focus-no-layout-shift inputs)
│   ├── Controls/               # WindowCaption, RevealablePasswordBox, SidebarTabButton…
│   ├── Themes/                 # Кольори, типографія, віджет-стилі (уніфікований
│   │                           #   Cut/Copy/Paste context menu)
│   ├── Platform/               # SingleInstance + Win32 P/Invoke (RegisterHotKey,
│   │                           #   SetForegroundWindow, GetForegroundWindow, SendInput)
│   └── Services/Migration/     # Legacy v1 → v2 config migration
├── tests/CapyBro.Tests/        # xUnit, понад 600 регресій
├── installer/
│   ├── installer.nsi           # NSIS-script
│   └── sign-installer.ps1      # Опціональний Authenticode signing wrapper
├── publish/win-x64/            # self-contained build output (folder) — gitignored
├── assets/                     # logo.ico, logo.png
├── scripts/                    # release.ps1, png-to-ico.ps1, etc.
└── README.md                   # цей файл
```

---

## 📊 Стан проєкту

- **Версія**: 2.0.0 (config schema v20)
- **Test suite**: понад 600 регресійних тестів (всі зелені, окрім known flake `ConcurrentSaveAndLoad_NoTornReadsOrErrorsAsync` що іноді тимчасово блокується testhost-ом з попереднього прогону)
- **QA-audit campaign**: закрита — всі Critical / High / Medium / Low findings оброблено (див. `.claude/test-audit/findings/PROGRESS.md` локально для деталей; директорія `.claude/` gitignored).
- **Polish iterations**: PromptPickerWindow повністю переписаний (foreground-window poller для cross-process dismiss, fix "first ↓ doesn't move selection", focus-keeper після chrome-кліків, multi-key handlers, ItemTemplate з ellipsis). Уніфіковано context menu по всіх input-полях. Прибрано layout-shift при focus у input-полях. WindowCaption кнопки (Min/Max/Close) тепер skip Tab navigation.
- **Post-campaign hardening passes (2026-05-12)**:
  - **Hotkey CTS-install race** у `App.WireRuntimeBehavior` — duplicate hotkey press під час in-flight run більше не disposable-зує cancel-handle першого запуску. Cancel-кнопка на toast тепер працює коректно навіть при double-tap. Заміна `Interlocked.Exchange + previous?.Dispose()` на `Interlocked.CompareExchange(slot, cts, null)` install pattern.
  - **ModelsDialog UX**: виправлено stale "no matches" статус коли користувач змінює фільтр з no-match → match.
  - **Sentinel dedupe across locale switches** у per-prompt model picker: при перемиканні мови UI попередня локалізація "Default model" sentinel більше не залишається у ComboBox як дублікат поряд з новою.
  - **Pixel-smooth scroll** на ModelsDialog / PromptPickerWindow / HistoryTab ListBox-ах: `ScrollViewer.CanContentScroll="False"` замінює item-based scroll.
  - **Full brand rename** (`AITextImprover` / `capy-bro` / `Capy Bro` → єдиний `CapyBro`).
  - **Homepage integration** — домен `https://capybro.app` (зареєстрований 2026-05-12) тепер вшито у п'ять точок взаємодії з користувачем, single C# source of truth у `Services/AppInfo.Homepage` (typed `Uri`) + `HomepageDisplay` (host-only label `"capybro.app"`):
    - **Settings sidebar footer** — single-line `v2.0.0 · capybro.app` рядок під тонким `BorderSubtleBrush` separator-ом. Обидва сегменти у `OnSurfaceMutedBrush` за замовчуванням; hover на лінк lifts у `BrandPrimaryBrush`, БЕЗ underline (3 iteration cycles before landing: muted bare text → globe icon + full-URL + underline → final single-line mid-dot strip).
    - **Onboarding wizard footer** — клікабельне `capybro.app` у центральній колонці bottom-border'а, рендериться один раз і присутнє на всіх 4 кроках візарда (Welcome / API key / Hotkeys / Done) без per-step duplication. Той самий quiet-by-default + hover-color-shift idiom.
    - **Installer Finish page** — `MUI_FINISHPAGE_SHOWREADME` checkbox **"Visit capybro.app"**, checked by default. Сидить поруч з існуючим uncheck-ed "Launch CapyBro" run-checkbox. MUI2's SHOWREADME-hook викликає `ShellExecute("open")` → браузер відкриває сайт після Finish.
    - **Add/Remove Programs** — installer пише `URLInfoAbout` + `HelpLink` HKCU Uninstall-key. Settings → Apps → CapyBro показує клікабельний support-link на capybro.app.
    - **README badges + footer** — hero-рядок з [capybro.app](https://capybro.app) + "Корисні посилання" + bottom-of-file capybro.app link.
- **Live tested**: PreserveLanguage, Cancel, hotkey collision, autostart repair, language picker autonyms, history undo, timeout toast, mid-stream timeout dismiss, ModelsDialog filter+scroll, per-prompt model dropdown across language switches, PromptPicker scroll — все проганялось проти deployed binary.

Артефакти типу `.claude/`, `bin/`, `obj/`, `publish/` — у `.gitignore`. Реліз-збірка робиться через `dotnet publish` + `makensis` (див. вище).

---

## 🗺 Roadmap

Нічого блокуючого; це wishlist-track наступних ітерацій:

- **L34 — Authenticode signing**: купити OV або EV сертифікат → виставити `CAPYBRO_SIGN_THUMBPRINT` → `sign-installer.ps1` automatically підписує кожну збірку. Це знімає SmartScreen warning і дозволяє users-without-technical-knowledge ставити без страху.
- **CI/CD**: GitHub Actions для auto-build + auto-release при push тегу `v*`. Зараз все локально.
- **Auto-update channel**: фоновий check на нові релізи у GitHub з опт-ін toast'ом "Update available". Без telemetry — просто GET до Releases API.
- **Button focus rings** (ButtonDefault/Primary/Destructive/IconOnly): зараз мають 1→2px transition на :IsKeyboardFocused, який ще не виправлений на constant-2px (як на Inputs). Якщо користувач поскаржиться — fix shape ідентичний.
- **Plugins / scripting**: можливість писати свої "промт-обгортки" як lua/js сценарії з доступом до variables (час, активна програма, виділений текст, clipboard).
- **macOS / Linux порт**: окремий проєкт через Avalonia. Залежить від попиту.

---

## 🤖 AI-driven workflow

Цей проєкт **повністю розроблений через [Claude Code](https://docs.claude.com/en/docs/claude-code/overview)** (Anthropic's CLI agent). Жодного коду не написано вручну — все згенеровано через діалог з агентом. Workflow:

1. Розгорнутий початковий brief описував усю архітектуру v2 з нуля.
2. Iterative development у git worktrees — кожна задача в окремій гілці.
3. Кожен фікс — окремий комміт + регресійний тест.
4. Quality gate (`dotnet format` + `build /warnaserror` + `test`) автоматично перед кожним коммітом.

Жодного IDE не потрібно — Claude Code сам читає/пише файли через інструменти, перегляд через `git diff`. Цей README, всі тести, всі коміти, brand-design, документація — все згенеровано в діалоговому режимі.

---

## 🔐 Code signing

Інсталятор зараз непідписаний — Windows SmartScreen показує warning. План для підпису:

1. Купити Authenticode-сертифікат:
   - **OV (Organisation Validated)** — ~$200/рік у Sectigo / DigiCert. Reputation у SmartScreen накопичується ~2-4 тижні.
   - **EV (Extended Validation)** — ~$300/рік. Миттєва репутація, але потрібен USB hardware token.
2. Імпортувати сертифікат у `Cert:\CurrentUser\My`.
3. Встановити змінну середовища `CAPYBRO_SIGN_THUMBPRINT` (SHA1-thumbprint).
4. Кожна збірка інсталятора:
   ```powershell
   & "C:\Program Files (x86)\NSIS\Bin\makensis.exe" installer\installer.nsi
   pwsh installer\sign-installer.ps1
   ```
   Скрипт сам викличе `signtool sign /sha1 $env:CAPYBRO_SIGN_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 …`.

Альтернативно — PFX-файл через `CAPYBRO_SIGN_CERT_PATH` + `CAPYBRO_SIGN_CERT_PASSWORD` (див. header `sign-installer.ps1`).

Без обох env var скрипт — documented no-op (local-dev builds не зламуються).

---

## 📚 Корисні посилання

- **Проєкт**: [capybro.app](https://capybro.app) · [GitHub repo](https://github.com/phantasmat2018/capy-bro) · [Releases](https://github.com/phantasmat2018/capy-bro/releases)
- **API провайдер**: [OpenRouter](https://openrouter.ai) · [OpenRouter API docs](https://openrouter.ai/docs)
- **Технології**: [.NET 8 docs](https://learn.microsoft.com/en-us/dotnet/) · [WPF docs](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/) · [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) · [NSIS docs](https://nsis.sourceforge.io/Docs/)
- **AI агент**: [Claude Code](https://docs.claude.com/en/docs/claude-code/overview)

---

## 🙏 Подяки

- **[OpenRouter](https://openrouter.ai)** — за єдиний API з десятків моделей. Без них довелось би тримати окрему інтеграцію з OpenAI / Anthropic / Google / Mistral. Жоден реальний user не хоче розбиратися з 5 API ключами.
- **[Lucide](https://lucide.dev)** (ISC license) — всі іконки у додатку. Stroke-based, узгодженого розміру, easy to swap.
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** — `[ObservableProperty]` source generator робить WPF MVVM приємним замість boilerplate-кошмару.
- **[Serilog](https://serilog.net)** — структуроване логування у текстовий sink.
- **[DiffPlex](https://github.com/mmanela/diffplex)** — side-by-side diff render для Diff preview modal.
- **[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)** — сучасний wrapper над WPF NotifyIcon з proper async/await.
- **[NSIS](https://nsis.sourceforge.io)** — installer toolkit що сам по собі — окрема скриптова мова, але працює.
- **Капібара** — як символ flow-стану і дружньої безконфліктності.

---

## 📜 Ліцензія

Код у цьому репозиторії — під ліцензією **MIT** (див. файл [`LICENSE`](LICENSE)): вільне використання, зміна та поширення, зокрема комерційне.

Назва й логотип «CapyBro», а також функції версії Pro — не входять у MIT-ліцензію.

---

<div align="center">
Made with ❤ і капібара-vibes through <a href="https://docs.claude.com/en/docs/claude-code/overview">Claude Code</a>.
<br>
<a href="https://capybro.app">capybro.app</a>
</div>
