using System.Collections.Frozen;
using System.ComponentModel;

using CapyBro.Models;

namespace CapyBro.Services;

public sealed class Translator : ITranslator
{
    // `internal` (not `private`) so tests can enforce dictionary parity
    // (Z7-F2 / H13) via the InternalsVisibleTo-configured test project.
    internal static readonly FrozenDictionary<Language, FrozenDictionary<string, string>> Strings =
        BuildStringTables();

    private static readonly Lazy<Translator> LazyInstance = new(() => new Translator());

    // Singleton seeds to English post-rebrand.  Pre-rebrand the default
    // was Ukrainian to match the team's primary locale; "CapyBro" ships
    // with English as the canonical default and locale-detect is no
    // longer wired into App.OnStartup.  SetLanguage(...) is still the
    // way to switch — the wizard and Settings → General both call it.
    private Language _language = Language.English;

    public static Translator Instance => LazyInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Language Language
    {
        get => _language;
        private set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            // "Item[]" is the WPF-recognized special name that refreshes every indexer binding
            // on this source — exactly what we need to live-switch all UI strings.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] => Resolve(key);

    public string Format(string key, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var template = Resolve(key);
        if (args.Length == 0)
        {
            return template;
        }

        // string.Format throws FormatException when:
        //   • the template has more {N} placeholders than args provided,
        //   • {N} indexes outside [0..args.Length-1],
        //   • the template contains an unbalanced "{" or "}".
        // Any of those bubble up as an unhandled exception in the binding
        // path (since most callers go through {Binding [some_key]}) and
        // crash the WPF UI element.  A localization typo or a translator
        // accidentally dropping "{0}" should never crash the surface that
        // displays it — fall back to the raw template so the user sees
        // the (fairly readable) "{0}-with-placeholders" string and the
        // exception is logged via the unhandled-exception sink rather
        // than tearing down a window.
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public void SetLanguage(Language language) => Language = language;

    /// <summary>
    /// Returns every locale's resolved value for <paramref name="key"/> as
    /// a deduplicated set.  Used by ViewModels that need to recognise a
    /// localized sentinel ACROSS locales — for instance the per-prompt
    /// "Default model" ComboBox in PromptsTabViewModel keeps an in-memory
    /// EditorModel string, and when the UI language switches the previous
    /// locale's sentinel must be treated as the SAME logical concept as
    /// the new locale's sentinel (otherwise the picker would surface both
    /// "Default model" and "Модель за замовчуванням" as separate entries).
    ///
    /// Static (not instance) because the underlying <see cref="Strings"/>
    /// table is invariant — the result is the same regardless of the
    /// currently-selected <see cref="Language"/>.
    /// </summary>
    public static IReadOnlySet<string> LocalizedValuesAcrossLocales(string key)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dict in Strings.Values)
        {
            if (dict.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            {
                values.Add(v);
            }
        }

        return values;
    }

    private string Resolve(string key)
    {
        if (Strings[_language].TryGetValue(key, out var value))
        {
            return value;
        }

        // Z7-F5 fix: post-rebrand the canonical default is English, so
        // a missing key in (say) Russian should fall back to English first,
        // not Ukrainian.  Cascade target → English → Ukrainian → key so we
        // never lose user-visible string just because a translator forgot
        // one of UA/RU; English is now the most-complete language slot.
        if (_language != Language.English
            && Strings[Language.English].TryGetValue(key, out var englishFallback))
        {
            return englishFallback;
        }

        if (_language != Language.Ukrainian
            && Strings[Language.Ukrainian].TryGetValue(key, out var ukrainianFallback))
        {
            return ukrainianFallback;
        }

        return key;
    }

    private static FrozenDictionary<Language, FrozenDictionary<string, string>> BuildStringTables()
    {
        var ukrainian = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Settings window — title shows the brand wordmark on the
            // caption bar (and Alt+Tab / taskbar), matching the marketing
            // material.  Tab names (tab_general / tab_prompts / tab_history)
            // remain localised below; the brand name itself does not
            // translate, so all three locale tables resolve to "CapyBro".
            ["settings_title"] = "CapyBro",
            ["tab_general"] = "Налаштування",
            ["tab_prompts"] = "Промти",
            ["tab_history"] = "Історія",
            ["language"] = "Мова",
            ["api_key"] = "API-ключ OpenRouter",
            ["api_key_hint"] = "Отримати ключ на openrouter.ai",
            ["model"] = "Модель",
            ["hotkey"] = "Гаряча клавіша",
            ["menu_hotkey"] = "Гаряча клавіша меню",
            ["autostart"] = "Запускати з Windows",
            ["browse_models"] = "Завантажити моделі",
            ["reset_settings"] = "Скинути налаштування",
            ["default_prompt"] = "Промт за замовчуванням",
            ["prompt_name"] = "Назва",
            ["prompt_text"] = "Текст",
            ["preserve_language"] = "Зберігати мову вхідного тексту",
            ["show_diff_preview"] = "Показувати превʼю різниці перед заміною",

            // Buttons / common
            ["btn_ok"] = "Гаразд",
            ["btn_cancel"] = "Скасувати",
            ["btn_delete"] = "Видалити",
            ["btn_new"] = "Новий",

            // Toasts
            ["toast_processing"] = "Обробка…",
            ["toast_done"] = "Готово",
            ["toast_error"] = "Помилка",
            ["toast_no_selection"] = "Виділіть текст і спробуйте знову",

            // API errors
            ["api_no_credits"] = "Недостатньо кредитів на OpenRouter",
            ["api_model_gated"] = "Модель недоступна для вашого регіону або акаунту",
            ["api_server_error"] = "Сервер OpenRouter тимчасово недоступний",
            ["api_request_timeout"] = "Перевищено час очікування відповіді",
            ["api_unauthorized"] = "Недійсний API-ключ",
            ["api_rate_limited"] = "Забагато запитів — спробуйте пізніше",
            ["api_bad_request"] = "Помилковий запит до API",
            ["api_unknown_error"] = "Невідома помилка API",
            ["api_empty_result"] = "Порожній результат після обробки",

            // Misc messages (with format args)
            ["msg_autostart_fail"] = "Не вдалося налаштувати автозапуск: {0}",
            ["msg_models_loaded"] = "Завантажено моделей: {0}",
            ["msg_models_search_empty"] = "Нічого не знайдено за вашим запитом",
            ["msg_model_added"] = "Модель {0} додано",
            ["msg_model_already_in_list"] = "Модель {0} вже у списку",
            ["msg_model_not_found"] = "Модель {0} не знайдено в каталозі OpenRouter",
            ["msg_model_validate_failed"] = "Не вдалося перевірити модель: {0}",

            // ─── New keys added during the QA-audit fix campaign ───
            // Z2-F2 / Z2-F5 / Z5-F3 / Z5-F4 / Z6-F3 / Z10-F1 / Z10-F3 / Z10-F4 / Z1-F4 / FZ3-F3 / Z4-F1 / Z7-F1 / Z9-F2 / FZ4-F2 / FZ2-F3
            ["msg_save_settings_failed"] = "Не вдалося зберегти налаштування. Перевірте, чи диск доступний для запису.",
            ["msg_reset_failed"] = "Не вдалося скинути налаштування. Перевірте журнал помилок.",
            ["msg_history_save_failed"] = "Не вдалося зберегти історію покращень.",
            ["msg_history_copy_failed"] = "Не вдалося скопіювати в буфер обміну.",
            ["msg_models_catalogue_empty"] = "Каталог моделей OpenRouter порожній — спробуйте оновити пізніше.",
            ["msg_api_key_persist_failed"] = "Не вдалося зберегти API-ключ у диспетчер облікових даних Windows.",
            ["msg_background_task_failed"] = "Сталася фонова помилка. Подробиці у файлі журналу.",
            ["msg_cancelled_with_result"] = "Скасовано. Результат залишився у буфері обміну — натисніть Ctrl+V, щоб вставити.",
            ["msg_model_not_configured"] = "Модель не вибрано. Відкрийте Налаштування → Загальне і виберіть модель.",
            ["api_network_unreachable"] = "Не вдалося підключитися до openrouter.ai. Перевірте інтернет-з'єднання.",
            ["api_tls_failure"] = "Не вдалося безпечно підключитися до openrouter.ai. З'єднання може бути заблоковане VPN або фаєрволом.",
            ["hotkey_register_failed"] = "Не вдалося зареєструвати гарячу клавішу {0} для «{1}» — вона зайнята іншим застосунком або конфліктує з іншим слотом.",
            ["tooltip_hotkey_conflict"] = "Конфліктує з «{0}»",
            ["tooltip_hotkey_unparseable"] = "«{0}» — не схоже на гарячу клавішу",
            ["toast_cancel"] = "Скасувати",
            ["caption_minimize"] = "Згорнути",
            ["caption_maximize"] = "Розгорнути",
            ["caption_restore"] = "Відновити",
            ["caption_close"] = "Закрити",
            // Language-picker autonyms — same string in every locale dictionary
            // (each language shows its own name in its own script), per FZ4-F2.
            ["lang_label_english"] = "English",
            ["lang_label_ukrainian"] = "Українська",
            ["lang_label_russian"] = "Русский",
            ["placeholder_search_models"] = "Пошук моделей…",
            ["confirm_reset_title"] = "Скинути всі налаштування?",
            ["confirm_reset_body"] = "Це очистить config-файл і API-ключ. Запис автозапуску в реєстрі НЕ буде змінений.",

            // Tray menu
            ["tray_settings"] = "Налаштування",
            ["tray_history"] = "Історія",
            ["tray_exit"] = "Вийти",

            // History window
            ["history_title"] = "Історія покращень",
            ["history_undo_done"] = "Оригінал відновлено",
            ["history_nothing_to_undo"] = "Немає що скасовувати",
            ["history_undo_hotkey"] = "Гаряча клавіша скасування",
            ["history_btn_copy_original"] = "Копіювати оригінал",
            ["history_btn_copy_improved"] = "Копіювати результат",
            ["history_btn_delete"] = "Видалити запис",
            ["history_btn_clear_all"] = "Очистити історію",
            ["history_confirm_clear_title"] = "Очистити всю історію?",
            ["history_confirm_clear_body"] = "Усі збережені записи буде видалено безповоротно.",
            ["prompt_confirm_delete_title"] = "Видалити промт?",
            ["prompt_confirm_delete_body"] = "Промт «{0}» буде видалено. Дію не можна скасувати.",
            ["history_label_original"] = "Оригінал",
            ["history_label_improved"] = "Результат",
            ["history_label_prompt"] = "Промт",

            // Hotkey conflict
            ["hotkey_conflict"] = "Комбінація {0} вже використовується для іншої дії",

            // Diff preview window
            ["diff_preview_title"] = "Перевірте зміни",
            ["diff_preview_subtitle"] = "Перевірте зміни, які зробив AI. Прийняти — вставити результат, Перегенерувати — спробувати ще раз, Відхилити — нічого не міняти.",
            ["diff_btn_accept"] = "Прийняти",
            ["diff_btn_regenerate"] = "Перегенерувати",
            ["diff_btn_reject"] = "Відхилити",

            // Experimental features section (General tab)
            ["experimental_features_section"] = "Додаткові функції",
            ["experimental_diff_preview"] = "Превʼю різниці перед застосуванням",
            ["experimental_diff_preview_hint"] = "Відкривати вікно порівняння для промтів з увімкненою опцією 'Показувати превʼю різниці'.",
            ["experimental_streaming"] = "Streaming відповідей",
            ["experimental_streaming_hint"] = "Показувати текст AI у тості, поки він генерується. Коли вимкнено — тост залишається статичним 'Обробка...' до завершення.",
            ["experimental_per_prompt_model"] = "Окрема модель для кожного промту",
            ["experimental_per_prompt_model_hint"] = "Дозволити кожному промту мати власну модель. Коли вимкнено — для всіх промтів використовується глобальна модель.",
            ["prompt_model_override"] = "Модель для цього промту",
            ["prompt_model_override_hint"] = "Виберіть модель або «Модель за замовчуванням» для використання глобальної.",
            ["prompt_model_default_option"] = "Модель за замовчуванням",
            ["new_prompt_default_name"] = "Новий промт",
            ["experimental_costs_and_credits"] = "Залишок кредитів та орієнтовна вартість",
            ["experimental_costs_and_credits_hint"] = "Показувати залишок на акаунті OpenRouter і додавати приблизну вартість запиту до тосту 'Обробка...'.",
            ["balance_label"] = "Залишок:",
            ["balance_loading"] = "Завантаження...",
            ["balance_format"] = "{0} з {1}",
            ["balance_no_api_key"] = "Введіть API-ключ для перегляду балансу",
            ["balance_unavailable"] = "Не вдалося отримати баланс",
            ["balance_refresh"] = "Оновити баланс",
            ["toast_cost_estimate"] = "(~{0})",
            ["experimental_privacy_redaction"] = "Маскування персональних даних",
            ["experimental_privacy_redaction_hint"] = "Замінювати email, URL, телефони та IBAN на маркери (наприклад «<<EMAIL_1>>») перед відправкою до AI; оригінали повертаються у відповіді локально. Знижує ризики передачі чутливих даних.",
            ["experimental_history"] = "Історія покращень",
            ["experimental_history_hint"] = "Зберігати кожне покращення в локальний журнал і показувати вкладку «Історія» на бічній панелі Налаштувань. Коли вимкнено — нічого не записується на диск і вкладка прихована.",
            ["experimental_keep_result_selected"] = "Залишати результат виділеним",
            ["experimental_keep_result_selected_hint"] = "Після вставки покращеного тексту автоматично виділяти його, щоб відразу можна було скопіювати, видалити або продовжити правку. Працює через UI Automation у сучасних редакторах (браузери, Office, VS) із резервним варіантом через Shift+Стрілка вліво для застарілих елементів керування.",
            ["beta_features_section"] = "Бета-функції",
            ["developer_mode_enabled_toast"] = "Режим розробника увімкнено",
            ["developer_mode_disabled_toast"] = "Режим розробника вимкнено",
            ["menu_cut"] = "Вирізати",
            ["menu_copy"] = "Копіювати",
            ["menu_paste"] = "Вставити",

            // Section headings (General tab card layout, Phase E)
            ["general_section_profile"] = "Профіль",
            ["general_section_hotkeys"] = "Гарячі клавіші",
            ["general_section_system"] = "Система",
            ["beta_features_warning"] = "Експериментальна функціональність — можливі баги",
            ["timeout_label"] = "Тайм-аут запиту (сек)",
            ["timeout_hint"] = "Скільки чекати відповіді від OpenRouter до скасування. За замовчуванням 60. Менше — швидше скасування під час повільної мережі; більше — менше переривань на великих відповідях. 0 — без обмеження часу (чекати скільки треба).",
            ["danger_zone_section"] = "Небезпечна зона",
            ["danger_zone_reset_hint"] = "Скидає всі налаштування програми до значень за замовчуванням. API-ключ і список моделей буде видалено. Дію не можна скасувати.",

            // Empty states (Phase E #6)
            ["empty_prompts_title"] = "Промтів ще немає",
            ["empty_prompts_body"] = "Натисніть «Новий», щоб створити перший промт. Він з'явиться в списку зліва і буде доступний для вибору як промт за замовчуванням.",
            // Z3-F3 / M7 — separate from empty_prompts_* so the editor pane's
            // empty-state can evolve independently of the list pane's
            // empty-state (e.g. future "deleted last prompt, choose
            // another or create new" wording differing from "no prompts
            // exist at all").
            ["empty_editor_title"] = "Промт не вибрано",
            ["empty_editor_body"] = "Виберіть промт зі списку зліва або натисніть «Новий», щоб створити.",
            ["empty_history_title"] = "Історія порожня",
            ["empty_history_body"] = "Кожне успішне покращення тексту з'являтиметься тут. Виділіть текст у будь-якій програмі та натисніть гарячу клавішу, щоб створити перший запис.",

            // Help-icon hints (Phase E #9)
            ["help_hotkeys"] = "Комбінація має містити хоча б один модифікатор (Ctrl, Alt, Shift або Win) і одну звичайну клавішу — наприклад «Ctrl+Shift+E». Кожен з трьох слотів повинен мати унікальну комбінацію, інакше зміна буде відхилена.",
            ["help_model"] = "Введіть ідентифікатор моделі OpenRouter (наприклад «openai/gpt-4o-mini» або «anthropic/claude-sonnet-4-5») і натисніть «+» для додавання. Кнопка хмари відкриває каталог усіх доступних моделей. Кнопка кошика видаляє обрану модель зі списку.",
            ["help_additional_features"] = "Опційні функції, які можна вмикати або вимикати окремо. Усі вони впливають на роботу програми лише коли активні. За замовчуванням вимкнені — щоб не ускладнювати інтерфейс новим користувачам.",

            // API key validation indicator (Phase E #10)
            ["api_key_status_checking"] = "Перевіряємо ключ...",
            ["api_key_status_valid"] = "Ключ дійсний",
            ["api_key_status_invalid"] = "Ключ недійсний",
            ["api_key_status_network"] = "Не вдалося перевірити ключ",

            // PromptPickerWindow keyboard hint (Phase E #4)
            ["picker_keyboard_hint"] = "↑↓ — навігація · Enter — вибрати · Esc — скасувати",
            ["prompts_section"] = "Промти",

            // ModelsDialog loading state (Phase E #14)
            ["msg_models_loading"] = "Завантажуємо каталог моделей...",

            // History date-bucket group headers (Phase E #11)
            ["history_bucket_today"] = "Сьогодні",
            ["history_bucket_yesterday"] = "Вчора",
            ["history_bucket_this_week"] = "Цього тижня",
            ["history_bucket_older"] = "Раніше",

            // History search + empty-state (Phase E #12)
            ["history_search_placeholder"] = "Пошук в історії...",
            ["history_no_matches"] = "Немає збігів",
            ["history_no_matches_body"] = "Спробуйте інший запит або очистіть пошук, щоб побачити всі записи.",
            ["models_no_matches"] = "Немає збігів",
            ["models_no_matches_body"] = "Спробуйте інший запит або очистіть пошук, щоб побачити всі моделі.",
            ["history_select_entry_title"] = "Виберіть запис у списку",
            ["history_select_entry_body"] = "Деталі вибраного покращення з'являться тут.",

            // PromptsTab editor placeholder (Phase E #15)
            ["prompt_text_placeholder"] = "Опишіть, що AI має зробити з виділеним текстом — наприклад «Виправ граматичні помилки, зберігаючи зміст і стиль» або «Переклади на англійську». Конкретні інструкції дають кращі результати.",

            // First-launch onboarding wizard
            ["onboarding_title"] = "Ласкаво просимо до CapyBro",
            ["onboarding_step_indicator"] = "Крок {0} з {1}",
            ["onboarding_btn_next"] = "Далі",
            ["onboarding_btn_back"] = "Назад",
            ["onboarding_btn_skip"] = "Пропустити",
            ["onboarding_btn_finish"] = "Готово",
            ["onboarding_welcome_heading"] = "Покращення тексту в один натиск клавіш",
            ["onboarding_welcome_body"] = "Виділіть текст у будь-якій програмі, натисніть гарячу клавішу — і AI перепише його за вашим промтом. Налаштуємо за хвилину.",
            ["onboarding_language_heading"] = "Оберіть мову інтерфейсу",
            ["onboarding_language_body"] = "Усі підказки та повідомлення будуть цією мовою. Її можна змінити пізніше в налаштуваннях.",
            ["onboarding_apikey_heading"] = "Введіть API-ключ OpenRouter",
            ["onboarding_apikey_body"] = "Ключ потрібен для звернень до моделей. Безкоштовний акаунт дає доступ до десятків моделей різних провайдерів.",
            ["onboarding_hotkey_heading"] = "Гаряча клавіша покращення",
            ["onboarding_hotkey_body"] = "Ця комбінація запустить покращення виділеного тексту в будь-якій програмі. Можна змінити пізніше.",
            ["onboarding_done_heading"] = "Готово!",
            ["onboarding_done_body"] = "Виділіть будь-який текст, натисніть «{0}» — і AI зробить решту. Програма живе в треї біля годинника. Натисніть на її іконку правою кнопкою, щоб відкрити налаштування.",

            // Prompts editor — error surfaces
            ["prompt_name_collision"] = "Промт із такою назвою вже існує. Назву повернуто до «{0}».",

            // Tooltips
            ["tooltip_add_model"] = "Додати модель зі списку",
            ["tooltip_remove_model"] = "Видалити обрану модель",
            ["tooltip_browse_models"] = "Завантажити каталог моделей з OpenRouter",
            ["tooltip_toggle_password"] = "Показати або сховати API-ключ",

            // ─── v15 Ollama-provider integration ───
            ["general_section_provider"] = "Провайдер",
            ["help_provider"] = "OpenRouter надсилає текст у хмару (потрібен API-ключ і кошти). Ollama працює локально — нічого не залишає вашого комп'ютера.",
            ["provider_use_ollama"] = "Використовувати локальну модель (Ollama)",
            ["provider_hint"] = "Без галочки — OpenRouter (хмара, потрібен API-ключ і платежі). З галочкою — Ollama: запити обробляє локальний сервер «ollama serve», нічого не виходить за межі комп'ютера.",
            ["general_section_ollama"] = "Локальні моделі (Ollama)",
            ["help_ollama"] = "Адреса локального Ollama-сервера. Стандартно це http://localhost:11434. Натисніть кнопку оновлення, щоб побачити моделі, які ви вже скачали через «ollama pull».",
            ["ollama_endpoint"] = "Адреса",
            // Split into prefix/suffix so XAML can render the word
            // "Ollama" itself as the clickable Hyperlink in the middle
            // (same pattern as the OpenRouter signup link under the
            // API-key field).  Brand name is hardcoded in the view
            // because it doesn't translate.  No surrounding spaces in
            // the strings: WPF's TextBlock inline parser inserts a
            // single space between sibling <Run>/<Hyperlink> elements
            // that sit on separate XAML lines, so adding spaces here
            // too would produce a visible double-space around the
            // brand link.
            ["ollama_hint_prefix"] = "Спочатку запустіть",
            ["ollama_hint_suffix"] = "і завантажте будь-яку модель через «ollama pull <назва>» (наприклад, gemma3 або mistral). Тоді натисніть кнопку оновлення поруч із вибором моделі.",
            ["tooltip_refresh_ollama_models"] = "Оновити список локальних моделей",
            ["msg_ollama_models_refreshed"] = "Знайдено локальних моделей: {0}",
            ["msg_ollama_model_not_configured"] = "Не обрано локальну модель Ollama. Відкрийте Налаштування → Локальні моделі.",
            ["ollama_unreachable"] = "Не вдалося підключитися до Ollama. Перевірте, чи запущено «ollama serve» і чи правильна адреса.",
            ["ollama_switched_to_openrouter"] = "Програму успішно перемкнуто на OpenRouter. Запустіть «ollama serve» і ввімкніть локальну модель знову, коли захочете.",
            ["ollama_model_not_pulled"] = "Модель {0} не встановлена локально. Виконайте «ollama pull {0}» у терміналі.",
            ["onboarding_apikey_ollama_hint"] = "Не маєте ключа OpenRouter? Пропустіть цей крок — після завершення можна перемкнутися на локальну Ollama в Налаштуваннях → Провайдер.",
        };

        var russian = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings_title"] = "CapyBro",
            ["tab_general"] = "Настройки",
            ["tab_prompts"] = "Промпты",
            ["tab_history"] = "История",
            ["language"] = "Язык",
            ["api_key"] = "API-ключ OpenRouter",
            ["api_key_hint"] = "Получить ключ на openrouter.ai",
            ["model"] = "Модель",
            ["hotkey"] = "Горячая клавиша",
            ["menu_hotkey"] = "Горячая клавиша меню",
            ["autostart"] = "Запускать с Windows",
            ["browse_models"] = "Загрузить модели",
            ["reset_settings"] = "Сбросить настройки",
            ["default_prompt"] = "Промпт по умолчанию",
            ["prompt_name"] = "Название",
            ["prompt_text"] = "Текст",
            ["preserve_language"] = "Сохранять язык исходного текста",
            ["show_diff_preview"] = "Показывать превью различий перед заменой",

            ["btn_ok"] = "ОК",
            ["btn_cancel"] = "Отмена",
            ["btn_delete"] = "Удалить",
            ["btn_new"] = "Новый",

            ["toast_processing"] = "Обработка…",
            ["toast_done"] = "Готово",
            ["toast_error"] = "Ошибка",
            ["toast_no_selection"] = "Выделите текст и попробуйте снова",

            ["api_no_credits"] = "Недостаточно кредитов на OpenRouter",
            ["api_model_gated"] = "Модель недоступна для вашего региона или аккаунта",
            ["api_server_error"] = "Сервер OpenRouter временно недоступен",
            ["api_request_timeout"] = "Превышено время ожидания ответа",
            ["api_unauthorized"] = "Недействительный API-ключ",
            ["api_rate_limited"] = "Слишком много запросов — попробуйте позже",
            ["api_bad_request"] = "Ошибочный запрос к API",
            ["api_unknown_error"] = "Неизвестная ошибка API",
            ["api_empty_result"] = "Пустой результат после обработки",

            ["msg_autostart_fail"] = "Не удалось настроить автозапуск: {0}",
            ["msg_models_loaded"] = "Загружено моделей: {0}",
            ["msg_models_search_empty"] = "Ничего не найдено по вашему запросу",
            ["msg_model_added"] = "Модель {0} добавлена",
            ["msg_model_already_in_list"] = "Модель {0} уже в списке",
            ["msg_model_not_found"] = "Модель {0} не найдена в каталоге OpenRouter",
            ["msg_model_validate_failed"] = "Не удалось проверить модель: {0}",

            // ─── New keys added during the QA-audit fix campaign ───
            ["msg_save_settings_failed"] = "Не удалось сохранить настройки. Проверьте, доступен ли диск для записи.",
            ["msg_reset_failed"] = "Не удалось сбросить настройки. Проверьте журнал ошибок.",
            ["msg_history_save_failed"] = "Не удалось сохранить историю улучшений.",
            ["msg_history_copy_failed"] = "Не удалось скопировать в буфер обмена.",
            ["msg_models_catalogue_empty"] = "Каталог моделей OpenRouter пуст — попробуйте обновить позже.",
            ["msg_api_key_persist_failed"] = "Не удалось сохранить API-ключ в диспетчер учётных данных Windows.",
            ["msg_background_task_failed"] = "Произошла фоновая ошибка. Подробности в файле журнала.",
            ["msg_cancelled_with_result"] = "Отменено. Результат остался в буфере обмена — нажмите Ctrl+V, чтобы вставить.",
            ["msg_model_not_configured"] = "Модель не выбрана. Откройте Настройки → Общее и выберите модель.",
            ["api_network_unreachable"] = "Не удалось подключиться к openrouter.ai. Проверьте интернет-соединение.",
            ["api_tls_failure"] = "Не удалось безопасно подключиться к openrouter.ai. Соединение может быть заблокировано VPN или фаерволом.",
            ["hotkey_register_failed"] = "Не удалось зарегистрировать горячую клавишу {0} для «{1}» — она занята другим приложением или конфликтует с другим слотом.",
            ["tooltip_hotkey_conflict"] = "Конфликтует с «{0}»",
            ["tooltip_hotkey_unparseable"] = "«{0}» — не похоже на горячую клавишу",
            ["toast_cancel"] = "Отмена",
            ["caption_minimize"] = "Свернуть",
            ["caption_maximize"] = "Развернуть",
            ["caption_restore"] = "Восстановить",
            ["caption_close"] = "Закрыть",
            ["lang_label_english"] = "English",
            ["lang_label_ukrainian"] = "Українська",
            ["lang_label_russian"] = "Русский",
            ["placeholder_search_models"] = "Поиск моделей…",
            ["confirm_reset_title"] = "Сбросить все настройки?",
            ["confirm_reset_body"] = "Это очистит config-файл и API-ключ. Запись автозапуска в реестре НЕ будет изменена.",

            ["tray_settings"] = "Настройки",
            ["tray_history"] = "История",
            ["tray_exit"] = "Выйти",

            ["history_title"] = "История улучшений",
            ["history_undo_done"] = "Оригинал восстановлен",
            ["history_nothing_to_undo"] = "Нечего отменять",
            ["history_undo_hotkey"] = "Горячая клавиша отмены",
            ["history_btn_copy_original"] = "Копировать оригинал",
            ["history_btn_copy_improved"] = "Копировать результат",
            ["history_btn_delete"] = "Удалить запись",
            ["history_btn_clear_all"] = "Очистить историю",
            ["history_confirm_clear_title"] = "Очистить всю историю?",
            ["history_confirm_clear_body"] = "Все сохранённые записи будут удалены безвозвратно.",
            ["prompt_confirm_delete_title"] = "Удалить промпт?",
            ["prompt_confirm_delete_body"] = "Промпт «{0}» будет удалён. Действие нельзя отменить.",
            ["history_label_original"] = "Оригинал",
            ["history_label_improved"] = "Результат",
            ["history_label_prompt"] = "Промпт",

            ["hotkey_conflict"] = "Комбинация {0} уже используется для другого действия",

            ["diff_preview_title"] = "Проверьте изменения",
            ["diff_preview_subtitle"] = "Проверьте изменения, которые сделал AI. Принять — вставить результат, Перегенерировать — попробовать снова, Отклонить — ничего не менять.",
            ["diff_btn_accept"] = "Принять",
            ["diff_btn_regenerate"] = "Перегенерировать",
            ["diff_btn_reject"] = "Отклонить",

            ["experimental_features_section"] = "Дополнительные функции",
            ["experimental_diff_preview"] = "Превью различий перед применением",
            ["experimental_diff_preview_hint"] = "Открывать окно сравнения для промптов с включённой опцией 'Показывать превью различий'.",
            ["experimental_streaming"] = "Streaming ответов",
            ["experimental_streaming_hint"] = "Показывать текст AI в тосте по мере генерации. Когда выключено — тост остаётся статичным 'Обработка...' до завершения.",
            ["experimental_per_prompt_model"] = "Отдельная модель для каждого промпта",
            ["experimental_per_prompt_model_hint"] = "Разрешить каждому промпту иметь свою модель. Когда выключено — для всех промптов используется глобальная модель.",
            ["prompt_model_override"] = "Модель для этого промпта",
            ["prompt_model_override_hint"] = "Выберите модель или «Модель по умолчанию» для использования глобальной.",
            ["prompt_model_default_option"] = "Модель по умолчанию",
            ["new_prompt_default_name"] = "Новый промпт",
            ["experimental_costs_and_credits"] = "Остаток кредитов и ориентировочная стоимость",
            ["experimental_costs_and_credits_hint"] = "Показывать остаток на аккаунте OpenRouter и добавлять примерную стоимость запроса к тосту 'Обработка...'.",
            ["balance_label"] = "Остаток:",
            ["balance_loading"] = "Загрузка...",
            ["balance_format"] = "{0} из {1}",
            ["balance_no_api_key"] = "Введите API-ключ для просмотра баланса",
            ["balance_unavailable"] = "Не удалось получить баланс",
            ["balance_refresh"] = "Обновить баланс",
            ["toast_cost_estimate"] = "(~{0})",
            ["experimental_privacy_redaction"] = "Маскирование персональных данных",
            ["experimental_privacy_redaction_hint"] = "Заменять email, URL, телефоны и IBAN на маркеры (например «<<EMAIL_1>>») перед отправкой в AI; оригиналы возвращаются в ответе локально. Снижает риски передачи чувствительных данных.",
            ["experimental_history"] = "История улучшений",
            ["experimental_history_hint"] = "Сохранять каждое улучшение в локальный журнал и показывать вкладку «История» на боковой панели Настроек. Когда выключено — ничего не записывается на диск и вкладка скрыта.",
            ["experimental_keep_result_selected"] = "Оставлять результат выделенным",
            ["experimental_keep_result_selected_hint"] = "После вставки улучшенного текста автоматически выделять его, чтобы сразу можно было скопировать, удалить или продолжить правку. Работает через UI Automation в современных редакторах (браузеры, Office, VS) с резервным вариантом через Shift+Стрелка влево для устаревших элементов управления.",
            ["beta_features_section"] = "Бета-функции",
            ["developer_mode_enabled_toast"] = "Режим разработчика включен",
            ["developer_mode_disabled_toast"] = "Режим разработчика отключен",
            ["menu_cut"] = "Вырезать",
            ["menu_copy"] = "Копировать",
            ["menu_paste"] = "Вставить",

            // Section headings (General tab card layout, Phase E)
            ["general_section_profile"] = "Профиль",
            ["general_section_hotkeys"] = "Горячие клавиши",
            ["general_section_system"] = "Система",
            ["beta_features_warning"] = "Экспериментальная функциональность — возможны баги",
            ["timeout_label"] = "Тайм-аут запроса (сек)",
            ["timeout_hint"] = "Сколько ждать ответа от OpenRouter до отмены. По умолчанию 60. Меньше — быстрее отмена при медленной сети; больше — меньше прерываний на больших ответах. 0 — без ограничения времени (ждать сколько потребуется).",
            ["danger_zone_section"] = "Опасная зона",
            ["danger_zone_reset_hint"] = "Сбрасывает все настройки приложения до значений по умолчанию. API-ключ и список моделей будут удалены. Действие нельзя отменить.",

            // Empty states (Phase E #6)
            ["empty_prompts_title"] = "Промптов ещё нет",
            ["empty_prompts_body"] = "Нажмите «Новый», чтобы создать первый промпт. Он появится в списке слева и будет доступен для выбора как промпт по умолчанию.",
            ["empty_editor_title"] = "Промпт не выбран",
            ["empty_editor_body"] = "Выберите промпт из списка слева или нажмите «Новый», чтобы создать.",
            ["empty_history_title"] = "История пуста",
            ["empty_history_body"] = "Каждое успешное улучшение текста будет появляться здесь. Выделите текст в любой программе и нажмите горячую клавишу, чтобы создать первую запись.",

            // Help-icon hints (Phase E #9)
            ["help_hotkeys"] = "Комбинация должна содержать хотя бы один модификатор (Ctrl, Alt, Shift или Win) и одну обычную клавишу — например «Ctrl+Shift+E». Каждый из трёх слотов должен иметь уникальную комбинацию, иначе изменение будет отклонено.",
            ["help_model"] = "Введите идентификатор модели OpenRouter (например «openai/gpt-4o-mini» или «anthropic/claude-sonnet-4-5») и нажмите «+» для добавления. Кнопка облака открывает каталог всех доступных моделей. Кнопка корзины удаляет выбранную модель из списка.",
            ["help_additional_features"] = "Опциональные функции, которые можно включать или выключать по отдельности. Все они влияют на работу программы только когда активны. По умолчанию выключены — чтобы не усложнять интерфейс новым пользователям.",

            // API key validation indicator (Phase E #10)
            ["api_key_status_checking"] = "Проверяем ключ...",
            ["api_key_status_valid"] = "Ключ действителен",
            ["api_key_status_invalid"] = "Ключ недействителен",
            ["api_key_status_network"] = "Не удалось проверить ключ",

            // PromptPickerWindow keyboard hint (Phase E #4)
            ["picker_keyboard_hint"] = "↑↓ — навигация · Enter — выбрать · Esc — отменить",
            ["prompts_section"] = "Промпты",

            // ModelsDialog loading state (Phase E #14)
            ["msg_models_loading"] = "Загружаем каталог моделей...",

            // History date-bucket group headers (Phase E #11)
            ["history_bucket_today"] = "Сегодня",
            ["history_bucket_yesterday"] = "Вчера",
            ["history_bucket_this_week"] = "На этой неделе",
            ["history_bucket_older"] = "Раньше",

            // History search + empty-state (Phase E #12)
            ["history_search_placeholder"] = "Поиск по истории...",
            ["history_no_matches"] = "Нет совпадений",
            ["history_no_matches_body"] = "Попробуйте другой запрос или очистите поиск, чтобы увидеть все записи.",
            ["models_no_matches"] = "Нет совпадений",
            ["models_no_matches_body"] = "Попробуйте другой запрос или очистите поиск, чтобы увидеть все модели.",
            ["history_select_entry_title"] = "Выберите запись из списка",
            ["history_select_entry_body"] = "Детали выбранного улучшения появятся здесь.",

            // PromptsTab editor placeholder (Phase E #15)
            ["prompt_text_placeholder"] = "Опишите, что AI должен сделать с выделенным текстом — например «Исправь грамматические ошибки, сохраняя смысл и стиль» или «Переведи на английский». Конкретные инструкции дают лучшие результаты.",

            // First-launch onboarding wizard
            ["onboarding_title"] = "Добро пожаловать в CapyBro",
            ["onboarding_step_indicator"] = "Шаг {0} из {1}",
            ["onboarding_btn_next"] = "Далее",
            ["onboarding_btn_back"] = "Назад",
            ["onboarding_btn_skip"] = "Пропустить",
            ["onboarding_btn_finish"] = "Готово",
            ["onboarding_welcome_heading"] = "Улучшение текста одним нажатием клавиш",
            ["onboarding_welcome_body"] = "Выделите текст в любой программе, нажмите горячую клавишу — и AI перепишет его по вашему промпту. Настроим за минуту.",
            ["onboarding_language_heading"] = "Выберите язык интерфейса",
            ["onboarding_language_body"] = "Все подсказки и сообщения будут на этом языке. Его можно изменить позже в настройках.",
            ["onboarding_apikey_heading"] = "Введите API-ключ OpenRouter",
            ["onboarding_apikey_body"] = "Ключ нужен для запросов к моделям. Бесплатный аккаунт даёт доступ к десяткам моделей разных провайдеров.",
            ["onboarding_hotkey_heading"] = "Горячая клавиша улучшения",
            ["onboarding_hotkey_body"] = "Эта комбинация запустит улучшение выделенного текста в любой программе. Можно изменить позже.",
            ["onboarding_done_heading"] = "Готово!",
            ["onboarding_done_body"] = "Выделите любой текст, нажмите «{0}» — и AI сделает остальное. Программа живёт в трее у часов. Кликните по её иконке правой кнопкой, чтобы открыть настройки.",

            // Prompts editor — error surfaces
            ["prompt_name_collision"] = "Промпт с таким именем уже существует. Имя возвращено к «{0}».",

            ["tooltip_add_model"] = "Добавить модель в список",
            ["tooltip_remove_model"] = "Удалить выбранную модель",
            ["tooltip_browse_models"] = "Загрузить каталог моделей из OpenRouter",
            ["tooltip_toggle_password"] = "Показать или скрыть API-ключ",

            // ─── v15 Ollama-provider integration ───
            ["general_section_provider"] = "Провайдер",
            ["help_provider"] = "OpenRouter отправляет текст в облако (нужен API-ключ и средства). Ollama работает локально — ничего не покидает ваш компьютер.",
            ["provider_use_ollama"] = "Использовать локальную модель (Ollama)",
            ["provider_hint"] = "Без галочки — OpenRouter (облако, нужен API-ключ и оплата). С галочкой — Ollama: запросы обрабатывает локальный сервер «ollama serve», ничего не выходит за пределы компьютера.",
            ["general_section_ollama"] = "Локальные модели (Ollama)",
            ["help_ollama"] = "Адрес локального Ollama-сервера. По умолчанию http://localhost:11434. Нажмите кнопку обновления, чтобы увидеть модели, которые вы уже скачали через «ollama pull».",
            ["ollama_endpoint"] = "Адрес",
            ["ollama_hint_prefix"] = "Сначала запустите",
            ["ollama_hint_suffix"] = "и загрузите любую модель через «ollama pull <название>» (например, gemma3 или mistral). Затем нажмите кнопку обновления рядом с выбором модели.",
            ["tooltip_refresh_ollama_models"] = "Обновить список локальных моделей",
            ["msg_ollama_models_refreshed"] = "Найдено локальных моделей: {0}",
            ["msg_ollama_model_not_configured"] = "Не выбрана локальная модель Ollama. Откройте Настройки → Локальные модели.",
            ["ollama_unreachable"] = "Не удалось подключиться к Ollama. Проверьте, запущена ли «ollama serve» и правильный ли адрес.",
            ["ollama_switched_to_openrouter"] = "Программа успешно переключена на OpenRouter. Запустите «ollama serve» и включите локальную модель снова, когда захотите.",
            ["ollama_model_not_pulled"] = "Модель {0} не установлена локально. Выполните «ollama pull {0}» в терминале.",
            ["onboarding_apikey_ollama_hint"] = "Нет ключа OpenRouter? Пропустите этот шаг — после завершения можно переключиться на локальную Ollama в Настройках → Провайдер.",
        };

        var english = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings_title"] = "CapyBro",
            ["tab_general"] = "Settings",
            ["tab_prompts"] = "Prompts",
            ["tab_history"] = "History",
            ["language"] = "Language",
            ["api_key"] = "OpenRouter API key",
            ["api_key_hint"] = "Get a key at openrouter.ai",
            ["model"] = "Model",
            ["hotkey"] = "Hotkey",
            ["menu_hotkey"] = "Menu hotkey",
            ["autostart"] = "Launch with Windows",
            ["browse_models"] = "Browse models",
            ["reset_settings"] = "Reset settings",
            ["default_prompt"] = "Default prompt",
            ["prompt_name"] = "Name",
            ["prompt_text"] = "Text",
            ["preserve_language"] = "Preserve source language",
            ["show_diff_preview"] = "Show diff preview before replacing",

            ["btn_ok"] = "OK",
            ["btn_cancel"] = "Cancel",
            ["btn_delete"] = "Delete",
            ["btn_new"] = "New",

            ["toast_processing"] = "Processing…",
            ["toast_done"] = "Done",
            ["toast_error"] = "Error",
            ["toast_no_selection"] = "Select some text and try again",

            ["api_no_credits"] = "Out of OpenRouter credits",
            ["api_model_gated"] = "Model is gated for your region or account",
            ["api_server_error"] = "OpenRouter server is temporarily unavailable",
            ["api_request_timeout"] = "Request timed out",
            ["api_unauthorized"] = "Invalid API key",
            ["api_rate_limited"] = "Too many requests — try again shortly",
            ["api_bad_request"] = "Bad API request",
            ["api_unknown_error"] = "Unknown API error",
            ["api_empty_result"] = "Empty result after stripping",

            ["msg_autostart_fail"] = "Failed to configure autostart: {0}",
            ["msg_models_loaded"] = "Loaded {0} models",
            ["msg_models_search_empty"] = "Nothing found for your query",
            ["msg_model_added"] = "Model {0} added",
            ["msg_model_already_in_list"] = "Model {0} is already in the list",
            ["msg_model_not_found"] = "Model {0} not found in OpenRouter catalogue",
            ["msg_model_validate_failed"] = "Could not validate the model: {0}",

            // ─── New keys added during the QA-audit fix campaign ───
            ["msg_save_settings_failed"] = "Could not save settings. Make sure the disk is writable.",
            ["msg_reset_failed"] = "Reset failed. Check the log for details.",
            ["msg_history_save_failed"] = "Could not save the improvement history.",
            ["msg_history_copy_failed"] = "Could not copy to clipboard.",
            ["msg_models_catalogue_empty"] = "The OpenRouter model catalogue is empty — try refreshing later.",
            ["msg_api_key_persist_failed"] = "Could not save the API key to Windows Credential Manager.",
            ["msg_background_task_failed"] = "A background task failed. Check the log file for details.",
            ["msg_cancelled_with_result"] = "Cancelled. The result is on the clipboard — press Ctrl+V to paste.",
            ["msg_model_not_configured"] = "Model not configured. Open Settings → General and choose a model.",
            ["api_network_unreachable"] = "Could not reach openrouter.ai. Check your internet connection.",
            ["api_tls_failure"] = "Could not securely connect to openrouter.ai. The connection may be blocked by a VPN or firewall.",
            ["hotkey_register_failed"] = "Could not register hotkey {0} for \"{1}\" — it's taken by another app or conflicts with another slot.",
            ["tooltip_hotkey_conflict"] = "Conflicts with \"{0}\"",
            ["tooltip_hotkey_unparseable"] = "\"{0}\" doesn't look like a hotkey combination",
            ["toast_cancel"] = "Cancel",
            ["caption_minimize"] = "Minimize",
            ["caption_maximize"] = "Maximize",
            ["caption_restore"] = "Restore",
            ["caption_close"] = "Close",
            ["lang_label_english"] = "English",
            ["lang_label_ukrainian"] = "Українська",
            ["lang_label_russian"] = "Русский",
            ["placeholder_search_models"] = "Search models…",
            ["confirm_reset_title"] = "Reset all settings?",
            ["confirm_reset_body"] = "This clears the config file and API key. Autostart registry entry will NOT be changed.",

            ["tray_settings"] = "Settings",
            ["tray_history"] = "History",
            ["tray_exit"] = "Exit",

            ["history_title"] = "Improvement history",
            ["history_undo_done"] = "Original restored",
            ["history_nothing_to_undo"] = "Nothing to undo",
            ["history_undo_hotkey"] = "Undo hotkey",
            ["history_btn_copy_original"] = "Copy original",
            ["history_btn_copy_improved"] = "Copy improved",
            ["history_btn_delete"] = "Delete entry",
            ["history_btn_clear_all"] = "Clear history",
            ["history_confirm_clear_title"] = "Clear all history?",
            ["history_confirm_clear_body"] = "All saved entries will be permanently removed.",
            ["prompt_confirm_delete_title"] = "Delete prompt?",
            // FZ4-F4 / L31: typographic curly quotes (U+201C / U+201D)
            // match the care UA/RU take with guillemets «…».  Pre-fix this
            // entry used ASCII straight quotes while the other two locales
            // used proper «…» — visible polish drift between languages.
            ["prompt_confirm_delete_body"] = "The prompt “{0}” will be deleted. This cannot be undone.",
            ["history_label_original"] = "Original",
            ["history_label_improved"] = "Improved",
            ["history_label_prompt"] = "Prompt",

            ["hotkey_conflict"] = "Hotkey {0} is already used for another action",

            ["diff_preview_title"] = "Review changes",
            ["diff_preview_subtitle"] = "Review the AI's changes. Accept to paste, Regenerate to try again, Reject to leave the original untouched.",
            ["diff_btn_accept"] = "Accept",
            ["diff_btn_regenerate"] = "Regenerate",
            ["diff_btn_reject"] = "Reject",

            ["experimental_features_section"] = "Additional features",
            ["experimental_diff_preview"] = "Diff preview before applying",
            ["experimental_diff_preview_hint"] = "Open the comparison window for prompts that have 'Show diff preview' enabled.",
            ["experimental_streaming"] = "Streaming responses",
            ["experimental_streaming_hint"] = "Show the AI's text live in the toast as it's generated. When off, the toast stays as a static 'Processing...' until done.",
            ["experimental_per_prompt_model"] = "Per-prompt model override",
            ["experimental_per_prompt_model_hint"] = "Let each prompt set its own model. When off, every prompt uses the global model regardless of any per-prompt override.",
            ["prompt_model_override"] = "Model for this prompt",
            ["prompt_model_override_hint"] = "Pick a model or «Default model» to use the global one.",
            ["prompt_model_default_option"] = "Default model",
            ["new_prompt_default_name"] = "New prompt",
            ["experimental_costs_and_credits"] = "Credits balance and cost estimate",
            ["experimental_costs_and_credits_hint"] = "Show your OpenRouter account balance and append an approximate per-request cost to the 'Processing...' toast.",
            ["balance_label"] = "Balance:",
            ["balance_loading"] = "Loading...",
            ["balance_format"] = "{0} of {1}",
            ["balance_no_api_key"] = "Enter an API key to see the balance",
            ["balance_unavailable"] = "Could not fetch balance",
            ["balance_refresh"] = "Refresh balance",
            ["toast_cost_estimate"] = "(~{0})",
            ["experimental_privacy_redaction"] = "Privacy redaction",
            ["experimental_privacy_redaction_hint"] = "Replace emails, URLs, phone numbers, and IBANs with placeholders (e.g. «<<EMAIL_1>>») before sending to the AI; originals are restored in the response locally. Reduces the risk of leaking sensitive data.",
            ["experimental_history"] = "Improvement history",
            ["experimental_history_hint"] = "Record every successful improvement to a local log and show the History tab in the Settings sidebar. When off, nothing is written to disk and the tab is hidden.",
            ["experimental_keep_result_selected"] = "Keep result selected",
            ["experimental_keep_result_selected_hint"] = "After pasting the improved text, automatically re-select it so you can copy, delete, or keep editing right away. Uses UI Automation in modern editors (browsers, Office, VS) with a Shift+Left fallback for legacy controls.",
            ["beta_features_section"] = "Beta features",
            ["developer_mode_enabled_toast"] = "Developer mode enabled",
            ["developer_mode_disabled_toast"] = "Developer mode disabled",
            ["menu_cut"] = "Cut",
            ["menu_copy"] = "Copy",
            ["menu_paste"] = "Paste",

            // Section headings (General tab card layout, Phase E)
            ["general_section_profile"] = "Profile",
            ["general_section_hotkeys"] = "Hotkeys",
            ["general_section_system"] = "System",
            ["beta_features_warning"] = "Experimental — may have rough edges",
            ["timeout_label"] = "Request timeout (sec)",
            ["timeout_hint"] = "How long to wait for OpenRouter's response before cancelling. Default 60. Lower values cancel faster on slow networks; higher values reduce interruptions on long responses. 0 means wait indefinitely (no time limit).",
            ["danger_zone_section"] = "Danger zone",
            ["danger_zone_reset_hint"] = "Resets all app settings to defaults. Your API key and saved model list will be removed. This action cannot be undone.",

            // Empty states (Phase E #6)
            ["empty_prompts_title"] = "No prompts yet",
            ["empty_prompts_body"] = "Click «New» to create your first prompt. It will appear in the list on the left and will be available as the default prompt.",
            ["empty_editor_title"] = "No prompt selected",
            ["empty_editor_body"] = "Choose a prompt from the list on the left or click «New» to create one.",
            ["empty_history_title"] = "History is empty",
            ["empty_history_body"] = "Every successful text improvement will appear here. Select text in any application and press your hotkey to create the first entry.",

            // Help-icon hints (Phase E #9)
            ["help_hotkeys"] = "The combination must include at least one modifier (Ctrl, Alt, Shift, or Win) and one regular key — e.g. «Ctrl+Shift+E». Each of the three slots needs a unique combination, otherwise the change is rejected.",
            ["help_model"] = "Type an OpenRouter model id (e.g. «openai/gpt-4o-mini» or «anthropic/claude-sonnet-4-5») and click «+» to add it. The cloud button opens the full catalogue of available models. The trash button removes the selected model from your list.",
            ["help_additional_features"] = "Optional features that can be turned on or off independently. They only affect the app's behaviour while active. Off by default — to keep the interface simple for new users.",

            // API key validation indicator (Phase E #10)
            ["api_key_status_checking"] = "Checking key...",
            ["api_key_status_valid"] = "Key is valid",
            ["api_key_status_invalid"] = "Key is invalid",
            ["api_key_status_network"] = "Couldn't verify key",

            // PromptPickerWindow keyboard hint (Phase E #4)
            ["picker_keyboard_hint"] = "↑↓ — navigate · Enter — select · Esc — cancel",
            ["prompts_section"] = "Prompts",

            // ModelsDialog loading state (Phase E #14)
            ["msg_models_loading"] = "Loading model catalogue...",

            // History date-bucket group headers (Phase E #11)
            ["history_bucket_today"] = "Today",
            ["history_bucket_yesterday"] = "Yesterday",
            ["history_bucket_this_week"] = "This week",
            ["history_bucket_older"] = "Older",

            // History search + empty-state (Phase E #12)
            ["history_search_placeholder"] = "Search history...",
            ["history_no_matches"] = "No matches",
            ["history_no_matches_body"] = "Try a different query, or clear the search to see all entries.",
            ["models_no_matches"] = "No matches",
            ["models_no_matches_body"] = "Try a different query, or clear the search to see all models.",
            ["history_select_entry_title"] = "Select an entry from the list",
            ["history_select_entry_body"] = "Details of the selected improvement will appear here.",

            // PromptsTab editor placeholder (Phase E #15)
            ["prompt_text_placeholder"] = "Describe what the AI should do with the selected text — e.g. «Fix grammatical errors while preserving meaning and style» or «Translate to English». Specific instructions yield better results.",

            // First-launch onboarding wizard
            ["onboarding_title"] = "Welcome to CapyBro",
            ["onboarding_step_indicator"] = "Step {0} of {1}",
            ["onboarding_btn_next"] = "Next",
            ["onboarding_btn_back"] = "Back",
            ["onboarding_btn_skip"] = "Skip",
            ["onboarding_btn_finish"] = "Done",
            ["onboarding_welcome_heading"] = "Improve text with a single keystroke",
            ["onboarding_welcome_body"] = "Select text in any application, hit a hotkey, and the AI rewrites it using your prompt. Let's get you set up in under a minute.",
            ["onboarding_language_heading"] = "Choose your interface language",
            ["onboarding_language_body"] = "All hints and messages will appear in this language. You can change it later in settings.",
            ["onboarding_apikey_heading"] = "Enter your OpenRouter API key",
            ["onboarding_apikey_body"] = "The key is required to call the models. A free account gives you access to dozens of models across multiple providers.",
            ["onboarding_hotkey_heading"] = "Improvement hotkey",
            ["onboarding_hotkey_body"] = "This combination will trigger improvement of the selected text in any application. You can change it later.",
            ["onboarding_done_heading"] = "All set!",
            ["onboarding_done_body"] = "Select any text, press «{0}», and the AI will take care of the rest. The app lives in the system tray near the clock — right-click its icon to open settings.",

            // Prompts editor — error surfaces
            ["prompt_name_collision"] = "A prompt with this name already exists. Name reverted to «{0}».",

            ["tooltip_add_model"] = "Add model to the list",
            ["tooltip_remove_model"] = "Remove the selected model",
            ["tooltip_browse_models"] = "Fetch model catalogue from OpenRouter",
            ["tooltip_toggle_password"] = "Show or hide the API key",

            // ─── v15 Ollama-provider integration ───
            ["general_section_provider"] = "Provider",
            ["help_provider"] = "OpenRouter sends your text to the cloud (needs an API key and credits). Ollama runs locally — nothing ever leaves your machine.",
            ["provider_use_ollama"] = "Use a local model (Ollama)",
            ["provider_hint"] = "Unchecked — OpenRouter (cloud, needs an API key and credits). Checked — Ollama: requests are handled by a local «ollama serve» process, nothing leaves your machine.",
            ["general_section_ollama"] = "Local models (Ollama)",
            ["help_ollama"] = "Address of your local Ollama server. Defaults to http://localhost:11434. Click the refresh button to list the models you've pulled with «ollama pull».",
            ["ollama_endpoint"] = "Endpoint",
            ["ollama_hint_prefix"] = "First, install",
            ["ollama_hint_suffix"] = "and pull any model via «ollama pull <name>» (for example, gemma3 or mistral). Then click the refresh button next to the model picker.",
            ["tooltip_refresh_ollama_models"] = "Refresh the list of local models",
            ["msg_ollama_models_refreshed"] = "Found {0} local model(s)",
            ["msg_ollama_model_not_configured"] = "No Ollama model picked yet. Open Settings → Local models.",
            ["ollama_unreachable"] = "Couldn't reach Ollama. Check that «ollama serve» is running and the endpoint is correct.",
            ["ollama_switched_to_openrouter"] = "Successfully switched to OpenRouter. Start «ollama serve» and re-enable the local model whenever you want.",
            ["ollama_model_not_pulled"] = "Model {0} isn't installed locally. Run «ollama pull {0}» in a terminal.",
            ["onboarding_apikey_ollama_hint"] = "No OpenRouter key? Skip this step — once the wizard is done you can switch to local Ollama in Settings → Provider.",
        };

        return new Dictionary<Language, FrozenDictionary<string, string>>
        {
            [Language.Ukrainian] = ukrainian.ToFrozenDictionary(StringComparer.Ordinal),
            [Language.Russian] = russian.ToFrozenDictionary(StringComparer.Ordinal),
            [Language.English] = english.ToFrozenDictionary(StringComparer.Ordinal),
        }.ToFrozenDictionary();
    }
}
