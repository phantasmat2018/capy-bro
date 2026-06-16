<div align="center">

# 🦫 CapyBro

**AI-помічник для будь-якого тексту у будь-якій програмі — один хоткей, нуль трення.**

[![Website](https://img.shields.io/badge/website-capybro.app-2563eb?style=flat-square)](https://capybro.app)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square)](#-установка-для-користувачів)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://learn.microsoft.com/en-us/dotnet/)
[![Version](https://img.shields.io/badge/version-2.0.0-success?style=flat-square)](#)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](#-ліцензія)

[capybro.app](https://capybro.app) · [Документація](https://capybro.app/uk/docs/) · [Скачати](https://github.com/phantasmat2018/capy-bro/releases) · [Купити Pro — $19](https://capybro.app/#pricing)

<a href="https://apps.microsoft.com/detail/9N73FHJTW3M7">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft Store" width="220" />
</a>

</div>

> 📢 **Це Free-core OSS-mirror під ліцензією MIT.** Pro-функції (статистика, експорт історії, backup/restore, switch-model хоткей, premium prompt packs) — у платній версії, яка розповсюджується як єдиний інсталятор з [capybro.app](https://capybro.app/#pricing).
>
> Цей репозиторій містить **повну Free-tier** функціональність CapyBro v2.0 — все, що потрібно для базового щоденного використання.

---

## 🦫 Що це

**CapyBro** — це Windows tray-утиліта, що пускає AI у вашу повсякденну роботу з текстом без жодного зайвого кроку.

Виділили текст у Word, Chrome, VS Code, Slack, Notepad — будь-де → натиснули глобальний хоткей (`Ctrl+Shift+E` за замовчуванням) → AI переписав за вашим промтом → новий текст уже стоїть на місці виділеного. Ніяких вкладок браузера, copy-paste у ChatGPT і назад, ніяких ручних промптів щоразу.

Працює офлайн-як-можливо: один HTTPS-запит до [OpenRouter](https://openrouter.ai) (десятки моделей через єдиний акаунт — GPT-4o, Claude, Gemini, Llama, etc), решта — локально. Native .NET 8 / WPF, без браузера, без Electron. Сидить у системному треї і прокидається тільки на ваш хоткей.

> **Чому "CapyBro"?** Капібара — найхалявніша і найдружніша тварина. Вона нікуди не поспішає, всім допомагає, і ладнає з усіма. Утиліта, яка тихо сидить поруч і робить нудну роботу замість вас — саме той вайб.

---

## 📖 Зміст

- [✨ Що вміє (Free)](#-що-вміє-free)
- [💎 Що дає Pro](#-що-дає-pro)
- [⌨ Гарячі клавіші](#-гарячі-клавіші)
- [📥 Установка для користувачів](#-установка-для-користувачів)
- [🪟 Інтерфейс](#-інтерфейс)
- [⚙ Налаштування](#-налаштування)
- [🔁 Скидання до дефолтів](#-скидання-до-дефолтів)
- [🔐 Приватність і безпека](#-приватність-і-безпека)
- [❓ FAQ / Troubleshooting](#-faq--troubleshooting)
- [🚀 Швидкий старт для розробників](#-швидкий-старт-для-розробників)
- [🧩 Архітектура та tech stack](#-архітектура-та-tech-stack)
- [🤝 Contributing](#-contributing)
- [📜 Ліцензія](#-ліцензія)

---

## ✨ Що вміє (Free)

### 🎯 Основні можливості

- **Глобальний хоткей** (`Ctrl+Shift+E` за замовчуванням):
  виділили текст у Word / Chrome / VS Code / куди завгодно → натиснули → AI переписав за вашим стандартним промтом → готовий результат уже в документі. Працює над будь-якою програмою, що підтримує copy-paste.
- **Меню промтів** (`Ctrl+Shift+Q`):
  toast зі списком ваших промтів. Стрілки/Enter для вибору, Esc для скасування, цифри `1-9` як швидкий доступ.
- **Undo** (`Ctrl+Shift+Z`):
  миттєво відкочує останню заміну до оригінального тексту. Працює навіть якщо історія вимкнена.
- **Кастомні промти**:
  створюйте/редагуйте необмежено своїх сценаріїв. Кожен промт може мати власну модель (override default), окремо для OpenRouter і Ollama, флаг "зберегти мову оригіналу", власну температуру / max tokens, прев'ю різниці перед застосуванням.

### 📚 Робота з результатами

- **Історія покращень** (за замовчуванням увімкнено):
  останні 50 запусків — оригінал / результат / промт / модель / час. Пошук, групування по бакетах дат (Сьогодні / Вчора / Цього тижня / Раніше), copy original / copy improved / delete entry, Clear all. Можна вимкнути в Налаштуваннях → Додаткові функції.
- **Diff preview** (опціональний модал):
  side-by-side порівняння оригіналу + результату з підсвічуванням змін через [DiffPlex](https://github.com/mmanela/diffplex). Accept / Regenerate / Reject.
- **Тостове сповіщення** з потоковим відображенням генерації (streaming) — видно як AI типує. Кнопка ✕ скасовує запит миттєво.

### 🔌 Провайдер LLM — на ваш вибір

Два бекенди, переключаються однією галочкою у Налаштуваннях → Провайдер:

- **OpenRouter (хмара, за замовчуванням)** — швидко, широкий вибір моделей (GPT-4o, Claude, Gemini, Llama тощо), pay-as-you-go. Потрібен API-ключ з [openrouter.ai/keys](https://openrouter.ai/keys).
- **Ollama (локально)** — для тих, хто **категорично не хоче відправляти текст у хмару**. Запустіть [ollama](https://ollama.com), завантажте будь-яку модель (`ollama pull gemma3`), натисніть «Оновити моделі» — увесь pipeline працює офлайн.

Окремо для кожного провайдера зберігаються: поточна модель, список pinned моделей, per-request таймаут (60 с / 120 с), per-prompt override. Перемикання OpenRouter ↔ Ollama жоден набір не губить. **Авто-перемикання на OpenRouter, якщо Ollama зникає** — програма виявляє при наступній взаємодії і ставить червоний toast → зелений confirmation через 2.5 с, persisted на диск.

### 🎨 UX / Зручність

- **3 мови UI**: Українська, Російська, English. Перемикається мить-в-мить.
- **Темна тема** + custom WindowChrome caption.
- **Уніфіковане Cut/Copy/Paste context-menu** у всіх text-input полях.
- **Pixel-smooth scroll** у списках.
- **Стійкість до конфліктів буфера обміну** (clipboard manager, RDP, антивірус).
- **Автозапуск з Windows** (опційно).
- **Onboarding-візард з 4 кроків** при першому запуску (Welcome + Language → API-ключ → хоткеї × 3 → Done).

### 🧪 Експериментальні функції

Усі під чекбоксами у Налаштуваннях → Додаткові функції:

- **Privacy redaction**: авто-маскування PII (email, телефони, картки, IBAN, ПІБ) перед відправкою у модель. Текст відновлюється після відповіді. OpenRouter-only.
- **Cost estimator**: показує приблизну вартість запиту до OpenRouter + поточний баланс. OpenRouter-only.
- **Per-prompt model**: для кожного промту окрема модель.
- **Налаштовуваний таймаут запиту**: `0` = безкінечний.

---

## 💎 Що дає Pro

Pro — одноразова покупка $19 на [capybro.app](https://capybro.app/#pricing), розблоковує 5 фіч поверх Free:

| Pro-фіча | Що робить |
|---|---|
| 📊 **Статистика використання** | Окрема вкладка з лічильниками (total improvements, characters, spent), breakdown по моделях, 30-day activity chart |
| 💾 **Backup / Restore настройок** | Експорт усього config у portable JSON. API-ключ і ліцензія не включаються (per-machine) |
| 🔄 **Switch-model хоткей** | `Ctrl+Shift+M` циклічно перемикає між 2-3 закріпленими моделями, працює навіть під час running-запиту |
| 📦 **Premium Prompt Packs** | 5 курованих наборів × ~10 промтів (Legal / Marketing / Academic / Code Review / Business). Trilingual |
| 📤 **Експорт історії** | CSV / JSON експорт усіх 50 записів історії |

Купити: [capybro.app/#pricing](https://capybro.app/#pricing). Одноразова покупка через Gumroad ($19). 3 пристрої на ключ. 14 днів гарантії повернення коштів.

---

## ⌨ Гарячі клавіші

| Дія | За замовчуванням | Налаштувати |
|---|---|---|
| Запустити default-промт на виділеному тексті | `Ctrl+Shift+E` | Налаштування → Загальне |
| Відкрити меню промтів | `Ctrl+Shift+Q` | Налаштування → Загальне |
| Відмінити останню заміну (Undo) | `Ctrl+Shift+Z` | Налаштування → Загальне |
| Скасувати поточний запит | `Esc` (на toast) або `✕` button | — |
| Відкрити Налаштування | Лівий клік на tray-іконку | — |
| Відкрити Налаштування на вкладці Історія | Правий клік на tray → Історія | — |
| Quit | Правий клік на tray → Вийти | — |

Усі хоткеї реєструються через Win32 `RegisterHotKey` з `MOD_NOREPEAT`, тому працюють глобально поверх будь-якої програми. Конфлікти з системними / іншими утилітами визначаються одразу: onboarding-візард і Налаштування → Загальне підсвічують конфліктний хоткей червоним.

---

## 📥 Установка для користувачів

### Скачати

#### 🏪 Microsoft Store (рекомендовано)

Підписана збірка, автоматичні оновлення через Windows Update, нативна підтримка x64 + ARM64. Поточна версія у Store — 2.0.1 з autostart-фіксом.

<a href="https://apps.microsoft.com/detail/9N73FHJTW3M7">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft Store" width="220" />
</a>

#### 📦 Або через winget

Один рядок у PowerShell або терміналі — winget завантажить і встановить сам:

```powershell
winget install RomanTykhonenko.CapyBro
```

#### 💾 Або `.exe`-інсталятор з GitHub Releases

Скачайте файл `CapyBro-Setup-2.0.0.exe` (~49 MB) з [GitHub Releases](https://github.com/phantasmat2018/capy-bro/releases) або з [capybro.app](https://capybro.app). Інсталятор per-user — НЕ потребує адмін-прав, ставить у `%LOCALAPPDATA%\CapyBro\`.

> **SmartScreen попередження** (тільки для `.exe`-маршруту): інсталятор зараз непідписаний, тому Windows покаже "Невідомий видавець". Натисніть "Додатково" → "Все одно виконати". У Microsoft Store-збірці цього вікна немає — Microsoft підписує її своїм сертифікатом.

### Запустити інсталятор

Запустіть інсталятор → майстер у 3 кліки → готово. Іконка з'явиться у системному треї біля годинника.

### Перший запуск

Запуститься **onboarding-візард з 4 кроків**:

1. **Welcome + UI Language** — короткий інтро та селектор мови (English / Українська / Русский) з live-preview.
2. **API-ключ OpenRouter** — поле з [openrouter.ai/keys](https://openrouter.ai/keys). 400 мс debounce → автоматична `/credits` валідація. Можна пропустити і потім перемкнутись на Ollama.
3. **Хоткеї** — три поля (Improve / Menu / Undo) з виявленням конфліктів.
4. **Done** — підсумок як користуватись.

«Пропустити» зберігає лише прапор `OnboardingCompleted=true`, решта полів залишається дефолтною — можна заповнити пізніше у Налаштуваннях. «Готово» записує все, що ви ввели.

### Що далі

Tray-іконка → Settings → вкладка Промти. Створіть кілька промтів під свої сценарії.

---

## 🪟 Інтерфейс

### Системний трей

Іконка-капібара у системному треї біля годинника. Tooltip: `CapyBro` (OpenRouter) або `CapyBro · Ollama`. Між запусками хоткея у застосунку **немає головного вікна** — все доступно через трей.

- **Лівий клік на іконці** → відкриває Settings.
- **Правий клік на іконці** → контекстне меню: **Налаштування** / **Історія** / **Вийти**. «Вийти» — graceful shutdown (flush pending writes, cancel in-flight HTTP).

### Settings window

Sidebar з вкладками:

| Іконка | Вкладка | Видимість |
|---|---|---|
| ⚙️ | **Налаштування** (General) | Завжди |
| ✏️ | **Промти** (Prompts) | Завжди |
| 🕐 | **Історія** (History) | Якщо `ExperimentalHistory=true` (за замовчуванням так) |

У footer-і sidebar: `v2.0.0 · capybro.app` (клікабельний) + (якщо Ollama) outline-pill `Ollama`.

---

## ⚙ Налаштування

Файл: `%USERPROFILE%\.ai_text_improver_v2_config.json` (schema v20)

### General tab — картки

- **Провайдер** — checkbox `Use local model (Ollama)`. Видимий тільки коли `ollama serve` запущений.
- **Профіль** — мова UI, API-ключ OpenRouter, дефолтна модель + ModelsDialog для каталога.
- **Local models (Ollama)** — endpoint, model picker, refresh (видима тільки коли Ollama активна).
- **Гарячі клавіші** — Improve / Menu / Undo (3 ComboBox-и з custom-input).
- **Система** — checkbox `Запускати з Windows`.
- **Додаткові функції** — 6 чекбоксів (Diff preview, Streaming, Per-prompt model, Cost & credits [OR-only], Privacy redaction [OR-only], History) + Timeout TextBox.
- **Danger zone** — `Скинути налаштування`.

### Sentinel: `Timeout = 0` = безкінечний таймаут

З v14 значення `0` у полі Request timeout — це валідний sentinel "чекати скільки треба". `TextProcessor` перекладає `0` → `Timeout.InfiniteTimeSpan` перед передачею у клієнт. Зовнішнє скасування (user Cancel, OnExit) все ще працює.

---

## 🔁 Скидання до дефолтів

> 🎯 **Усе робиться через кнопки в UI. Файли руками видаляти не потрібно.**

### Скинути налаштування

**Settings → General → пролистати донизу → Danger zone → «Скинути налаштування»** → ConfirmDialog → YES.

Що відбувається:
- Config wipes до дефолтних значень
- API-ключ видаляється з Windows Credential Manager
- General + Prompts tabs reload з диску

**Чого Reset НЕ робить** (навмисно): не чіпає `~/.ai_text_improver_v2_history.json` (окрема кнопка `Settings → History → Clear all`), не чіпає Run-key автозапуску у HKCU (вимикається через `Settings → System → uncheck «Запускати з Windows»`), не чіпає файл логу.

### Знов побачити OnboardingWizard

Reset НЕ повертає OnboardingWizard — прапор `OnboardingCompleted=true` присутній в `AppConfig.Default`. Якщо потрібно: закрити застосунок → відкрити `~/.ai_text_improver_v2_config.json` у редакторі → змінити `"OnboardingCompleted": true` на `false` → перезапустити.

---

## 🔐 Приватність і безпека

CapyBro по-замовчуванню збирає ZERO телеметрії — жодних analytics, crash reporting, опитувань. Усі мережеві запити — це лише ваші запити до OpenRouter (один HTTPS-call на хоткей). Більше нічого нікуди не йде.

### Зберігання даних

| Що | Де | Як очистити |
|---|---|---|
| API ключ | **Windows Credential Manager** (`CapyBroV2`) — DPAPI під поточним користувачем | `Settings → Reset settings` (Danger zone) АБО Control Panel → Credential Manager |
| Конфіг | `~/.ai_text_improver_v2_config.json` (plaintext JSON, без API-ключа) | `Settings → Reset settings` |
| Історія | `~/.ai_text_improver_v2_history.json` (50 entries max) | `Settings → History → Clear all` |
| Логи | `~/.ai_text_improver_v2*.log` (diagnostic info, БЕЗ вмісту тексту/відповідей) | Видалити вручну |

> **Інваріант**: snake_case префікси `.ai_text_improver_v2_*` навмисно НЕ перейменовано при brand-rename `AITextImprover` → `CapyBro` (2026-05-12) — це би осиротило всі існуючі v1 установки.

### Privacy redaction (експериментально)

Опція **Налаштування → Додаткові функції → Маскування PII** автоматично замінює перед відправкою у модель: email → `<<EMAIL_n>>`, телефони → `<<PHONE_n>>`, URLs → `<<URL_n>>`, кредитні картки → `<<CARD_n>>`, IBAN → `<<IBAN_n>>`, ПІБ → `<<NAME_n>>`. Після відповіді AI оригінальні значення підставляються назад — модель ніколи не бачить реальних PII. Implementation: `Services/PrivacyRedactor.cs` + регресійні тести.

---

## ❓ FAQ / Troubleshooting

### Хоткей не реагує

1. Перевірте, чи цей хоткей не зайнятий іншою утилітою (Snipping Tool `Ctrl+Shift+S` часто конфліктує).
2. Налаштування → Загальне → блок «Гарячі клавіші». Конфлікт підсвічується червоним.
3. Змініть на щось унікальне типу `Ctrl+Alt+Shift+E`.

### AI повертає переклад замість виправлення / навпаки

Відкрийте промт у Налаштування → Промти і перепишіть його експліцитніше («Виправ помилки **тією ж мовою**, не перекладай»). Модель робить те, що каже промт.

### Toast зник, але новий текст не з'явився

- Перевірте, чи у цільовій програмі є фокус на текстовому полі. CapyBro вставляє через clipboard + `Ctrl+V`.
- Деякі sandbox'овані додатки (UWP / WSA) блокують keyboard automation. Результат уже у вашому clipboard — вставте `Ctrl+V` вручну.

### Як скасувати невдале покращення

Натисніть `Ctrl+Shift+Z` одразу після нежеданого результату. CapyBro поверне оригінал з in-memory кешу (або з історії).

### "Невідомий видавець" попередження

Інсталятор зараз непідписаний (SmartScreen accumulation period). Сейф — «Додатково» → «Все одно виконати».

### Settings вікно не відкривається

- Іконка у треї повинна бути жива. Якщо сіра — додаток впав. Перезапустіть з Start menu.
- Лог: `%USERPROFILE%\.ai_text_improver_v2*.log` — там stack trace.

### Працює на Windows 10?

Так, мінімум — Windows 10 1809. Mica → solid background на старіших.

### Працює на macOS / Linux?

Ні. Залежить від Win32 API (RegisterHotKey, SendInput, Credential Manager, NotifyIcon).

---

## 🚀 Швидкий старт для розробників

```powershell
# Клонувати
git clone https://github.com/phantasmat2018/capy-bro.git
cd capy-bro

# Запустити з-під dotnet (Debug)
dotnet run --project src/CapyBro

# Тести
dotnet test

# Self-contained збірка
dotnet publish src/CapyBro -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish/win-x64

# NSIS installer (потрібен makensis у PATH або у `Program Files (x86)\NSIS\Bin\`)
& "C:\Program Files (x86)\NSIS\Bin\makensis.exe" installer\installer.nsi
# → installer/CapyBro-Setup-2.0.0.exe
```

### Quality gate

Кожен коміт має проходити три перевірки:

```powershell
dotnet format --verify-no-changes   # стиль коду
dotnet build -warnaserror           # 0 warnings
dotnet test                         # regression tests
```

Усі троє мають бути зеленими — non-negotiable.

### Локальні гачки

- **Не запускайте `dotnet build` поки `CapyBro.exe` у треї** — DLL заблокована running процесом. Перед збіркою: `taskkill /F /IM CapyBro.exe /IM testhost.exe /IM dotnet.exe`.
- **InternalsVisibleTo для тестів** — `CapyBro.Tests` має доступ до `internal`-методів через атрибут у csproj.
- **Translator parity invariant** — будь-який новий ключ перекладу мусить бути в усіх 3 локалях (UA/RU/EN).

---

## 🧩 Архітектура та tech stack

| Layer | Технологія | Чому |
|---|---|---|
| UI | **WPF / .NET 8** | Native Windows, без Electron. Custom WindowChrome для DARK title bar. |
| MVVM | **CommunityToolkit.Mvvm** | `[ObservableProperty]`, `[RelayCommand]` — мінімум boilerplate. |
| DI | **Microsoft.Extensions.Hosting** | Generic Host для tray-апи. |
| Логування | **Serilog** | Структуроване, до файлу + дебаг. |
| Гарячі клавіші | **Win32 RegisterHotKey** (P/Invoke) | Глобальні. |
| Tray-іконка | **H.NotifyIcon** | Сучасніший wrapper навколо WPF NotifyIcon. |
| API | **OpenRouter** (HTTPS, SSE) | Доступ до десятків AI моделей через один акаунт. |
| Local LLM | **Ollama** (HTTP `/api/chat`, NDJSON) | Privacy-first, нічого не виходить за межі ПК. |
| Single-instance | Named `Mutex` + `EventWaitHandle` | Другий запуск активує перший. |
| Credential store | **Windows Credential Manager** (DPAPI) | API ключ ніколи не у JSON. |
| Configuration | JSON, schema v20, atomic save | tmp+File.Replace. |
| Diff render | **DiffPlex** | Side-by-side. |
| Іконки | **Lucide** (ISC) | 24×24 stroke-based. |
| Installer | **NSIS 3.10** | Per-user (no admin), MUI2. |

### Структура проєкту

```
capy-bro/
├── src/CapyBro/                # WPF додаток
│   ├── App.xaml(.cs)           # entry point, DI host, error handlers
│   ├── Models/                 # AppConfig (schema v20), HistoryEntry, Prompt, …
│   ├── Services/               # ConfigStore, HotkeyManager, OpenRouterClient,
│   │                           #   OllamaClient, TextProcessor, HistoryStore,
│   │                           #   AutostartService, Translator, ToastPresenter,
│   │                           #   PrivacyRedactor, …
│   ├── ViewModels/             # GeneralTabVM, PromptsTabVM, HistoryVM, …
│   ├── Views/                  # XAML вікна
│   ├── Controls/               # WindowCaption, RevealablePasswordBox, …
│   ├── Themes/                 # Кольори, типографія, віджет-стилі
│   ├── Platform/               # SingleInstance + Win32 P/Invoke
│   └── Services/Migration/     # Legacy v1 → v2 config migration
├── tests/CapyBro.Tests/        # xUnit
├── installer/                  # NSIS-script + sign-installer.ps1
├── assets/                     # logo.ico, logo.png, header.png
└── README.md                   # цей файл
```

---

## 🤝 Contributing

Pull requests welcome! Зверніть увагу:

1. **Quality gate non-negotiable** — `dotnet format --verify-no-changes`, `dotnet build -warnaserror`, `dotnet test` мають бути зеленими.
2. **Translator parity** — нові рядки потребують переклад у всі 3 локалі (UA/RU/EN) одразу.
3. **Тести assertion-by-value** — `Assert.Equal(expected, actual)` на user-intent slots, не `It.IsAny<>`.
4. **Жодних коментарів типу «WHAT does this code do»** — тільки WHY non-obvious.
5. **Жодних emoji у коді** (тільки у документації як ця).
6. Issues + bug reports — у [GitHub Issues](https://github.com/phantasmat2018/capy-bro/issues).

Pro-функції не приймаються через PR у цей репо (вони у платній закритій версії). Якщо ваша зміна — це Free-tier фіча або фікс, додавайте.

---

## 📜 Ліцензія

[MIT License](LICENSE) © 2026 Roman Tykhonenko.

Pro-версія (з статистикою, експортом, switch-model хоткеєм, premium prompt packs, backup/restore) розповсюджується окремо як платний product на [capybro.app](https://capybro.app/#pricing) і **не** покривається MIT-ліцензією цього репо.

---

<div align="center">
© 2026 CapyBro. Made in Ukraine with warmth.
<br>
<a href="https://capybro.app">capybro.app</a>
</div>
