using System.ComponentModel;

using CapyBro.Models;

namespace CapyBro.Services;

public interface ITranslator : INotifyPropertyChanged
{
    Language Language { get; }

    string this[string key] { get; }

    string Format(string key, params object[] args);

    void SetLanguage(Language language);
}
