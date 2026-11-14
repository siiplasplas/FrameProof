using FrameProof.Core.Util;

namespace FrameProof.Core.Tweaks;

public enum TweakState { NotApplied, Applied, PendingReboot, NotApplicable }

public abstract class Tweak
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    /// <summary>Ne yapar (somut olarak hangi ayar değişir).</summary>
    public abstract string WhatItDoes { get; }
    /// <summary>Dürüst beklenen etki.</summary>
    public abstract string Effect { get; }
    public virtual string? Risk => null;
    public virtual bool NeedsAdmin => false;
    public virtual bool NeedsReboot => false;
    /// <summary>Ana ekrandaki "Optimize et" tek tıkla uygulayabilir mi (riski ve yeniden başlatması yok).</summary>
    public virtual bool OneClick => false;
    /// <summary>Ayarlar sekmesinde hangi başlık altında durur.</summary>
    public virtual string Group => "Oyun ayarları";

    public abstract TweakState GetState();
    protected abstract void ApplyCore(List<ChangeRecord> changes);

    public virtual string StateText => GetState() switch
    {
        TweakState.Applied => "Uygulanmış",
        TweakState.PendingReboot => "Yeniden başlatma bekliyor",
        TweakState.NotApplicable => "Bu sistemde gerekmiyor",
        _ => "Uygulanmamış",
    };

    /// <summary>Uygular, eski değerleri günlüğe yazar. Yarıda hata olursa yapılanları geri alır.</summary>
    public JournalEntry Apply()
    {
        if (NeedsAdmin && !Admin.IsElevated)
            throw new UnauthorizedAccessException("Bu ayar için yönetici izni gerekiyor.");
        var changes = new List<ChangeRecord>();
        try
        {
            ApplyCore(changes);
        }
        catch
        {
            try { Journal.Revert(changes); } catch { }
            throw;
        }
        var e = new JournalEntry { TweakId = Id, Title = Title, NeedsReboot = NeedsReboot, Changes = changes };
        if (changes.Count > 0) Journal.Add(e);
        return e;
    }
}
