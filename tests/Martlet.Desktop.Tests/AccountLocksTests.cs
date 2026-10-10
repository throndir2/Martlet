using System.IO;
using System.Text;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AccountLocksTests : IDisposable
{
    private const int Fast = 1_000;
    private readonly string data = Path.Combine(Path.GetTempPath(), "martlet-account-locks-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly Guid sam = Guid.NewGuid();

    private AccountLockStore Store() => new(data, Fast, clock);

    [Fact]
    public void A_new_account_has_nothing_set_and_the_windows_login_unlocks_it()
    {
        var current = Store().Load(sam);
        Assert.Empty(current.Methods);
        Assert.True(current.Remember);
        Assert.False(current.NeedsUnlock(windowsLoginOfThisPc: true));
        Assert.True(current.NeedsUnlock(windowsLoginOfThisPc: false));
        Assert.Equal(AccountUnlockStatus.NotSet, Store().Unlock(sam, AccountUnlockMethod.Pin, "1234").Status);
    }

    [Fact]
    public void A_pin_unlocks_and_is_kept_only_as_a_protected_verifier()
    {
        var store = Store();
        store.SetPin(sam, "482913", null);
        Assert.Equal(["pin"], store.Load(sam).Methods);
        Assert.Equal(AccountUnlockStatus.Unlocked, store.Unlock(sam, AccountUnlockMethod.Pin, "482913").Status);
        Assert.Equal(AccountUnlockStatus.Wrong, store.Unlock(sam, AccountUnlockMethod.Pin, "000000").Status);
        var text = File.ReadAllText(store.PathFor(sam), Encoding.UTF8);
        Assert.DoesNotContain("482913", text);
        Assert.Contains("\"has_pin\": true", text);
        Assert.Contains("\"protected\"", text);
        Assert.DoesNotContain("\"salt\"", text);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("12a4")]
    public void A_pin_is_four_to_twelve_digits(string pin) =>
        Assert.Throws<ArgumentException>(() => Store().SetPin(sam, pin, null));

    [Fact]
    public void Wrong_tries_after_five_make_the_next_try_wait_and_a_right_one_resets_them()
    {
        var store = Store();
        store.SetPin(sam, "1234", null);
        for (var i = 0; i < AccountLockStore.FreeTries - 1; i++)
            Assert.Equal(AccountUnlockStatus.Wrong, store.Unlock(sam, AccountUnlockMethod.Pin, "9999").Status);
        var fifth = store.Unlock(sam, AccountUnlockMethod.Pin, "9999");
        Assert.Equal(AccountUnlockStatus.Wait, fifth.Status);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(1), fifth.RetryAfter);
        // While waiting even the right PIN isn't checked; the wait survives a new store (a restart).
        Assert.Equal(AccountUnlockStatus.Wait, Store().Unlock(sam, AccountUnlockMethod.Pin, "1234").Status);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(AccountUnlockStatus.Wait, store.Unlock(sam, AccountUnlockMethod.Pin, "9999").Status);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(2), store.Load(sam).RetryAfter);
        clock.Now += TimeSpan.FromSeconds(3);
        Assert.Equal(AccountUnlockStatus.Unlocked, store.Unlock(sam, AccountUnlockMethod.Pin, "1234").Status);
        Assert.Equal(0, store.Load(sam).Failures);
        Assert.Null(store.Load(sam).RetryAfter);
    }

    [Fact]
    public void The_longest_wait_is_fifteen_minutes()
    {
        var store = Store();
        store.SetPin(sam, "1234", null);
        for (var i = 0; i < 40; i++)
        {
            store.Unlock(sam, AccountUnlockMethod.Pin, "9999");
            clock.Now += AccountLockStore.LongestWait;
        }
        clock.Now -= AccountLockStore.LongestWait;
        Assert.Equal(clock.Now + AccountLockStore.LongestWait, store.Load(sam).RetryAfter);
    }

    [Fact]
    public void A_remembered_password_unlocks_offline_and_can_be_forgotten()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.RememberPassword(sam, "short", null));
        store.RememberPassword(sam, "correct horse battery", null);
        Assert.Equal(AccountUnlockStatus.Unlocked, store.Unlock(sam, AccountUnlockMethod.Password, "correct horse battery").Status);
        Assert.Equal(AccountUnlockStatus.Wrong, store.Unlock(sam, AccountUnlockMethod.Password, "wrong horse battery").Status);
        store.ForgetPassword(sam);
        Assert.Equal(AccountUnlockStatus.NotSet, store.Unlock(sam, AccountUnlockMethod.Password, "correct horse battery").Status);
    }

    [Fact]
    public void Asking_for_the_password_needs_a_way_to_unlock_and_turns_off_with_the_last_one()
    {
        var store = Store();
        Assert.Throws<InvalidOperationException>(() => store.Change(sam, l => l with { AskOnThisPc = true }));
        store.SetPin(sam, "1234", null);
        var asked = store.Change(sam, l => l with { AskOnThisPc = true });
        Assert.True(asked.NeedsUnlock(windowsLoginOfThisPc: true));
        Assert.False(store.RemovePin(sam).AskOnThisPc);
    }

    [Fact]
    public void Windows_hello_unlocks_only_when_it_is_turned_on_here()
    {
        var store = Store();
        Assert.Equal(AccountUnlockStatus.NotSet, store.Unlock(sam, AccountUnlockMethod.WindowsHello, null).Status);
        store.SetHello(sam, true);
        Assert.Equal(["hello"], store.Load(sam).Methods);
        Assert.Equal(AccountUnlockStatus.Unlocked, store.Unlock(sam, AccountUnlockMethod.WindowsHello, null).Status);
    }

    [Fact]
    public void Encryption_gives_a_file_key_that_the_pin_and_the_password_open_but_windows_hello_does_not()
    {
        var store = Store();
        store.SetPin(sam, "2468", null);
        store.RememberPassword(sam, "correct horse battery", null);
        store.SetHello(sam, true);
        Assert.Throws<InvalidOperationException>(() => store.TurnOnEncryption(sam, "1111", null));
        var (locked, key) = store.TurnOnEncryption(sam, "2468", "correct horse battery");
        Assert.True(locked.Encrypt);
        Assert.False(locked.Hello);
        Assert.True(locked.NeedsUnlock(windowsLoginOfThisPc: true));
        Assert.Equal(key, store.Unlock(sam, AccountUnlockMethod.Pin, "2468").FileKey);
        Assert.Equal(key, store.Unlock(sam, AccountUnlockMethod.Password, "correct horse battery").FileKey);
        Assert.Throws<InvalidOperationException>(() => store.SetHello(sam, true));
        Assert.Throws<InvalidOperationException>(() => store.RemovePin(sam));
        Assert.Throws<InvalidOperationException>(() => store.SetPin(sam, "1357", null));
        store.SetPin(sam, "1357", key);
        Assert.Equal(key, store.Unlock(sam, AccountUnlockMethod.Pin, "1357").FileKey);
        Assert.False(store.TurnOffEncryption(sam).Encrypt);
        Assert.Null(store.Unlock(sam, AccountUnlockMethod.Pin, "1357").FileKey);
    }

    [Fact]
    public void A_password_with_no_file_key_asks_for_the_pin()
    {
        var store = Store();
        store.SetPin(sam, "2468", null);
        store.RememberPassword(sam, "correct horse battery", null);
        store.TurnOnEncryption(sam, "2468", null);
        Assert.Equal(AccountUnlockStatus.NeedsPin, store.Unlock(sam, AccountUnlockMethod.Password, "correct horse battery").Status);
    }

    [Fact]
    public void All_lists_every_account_with_an_unlock_file_and_delete_removes_one()
    {
        var store = Store();
        var alex = Guid.NewGuid();
        store.SetPin(sam, "1234", null);
        store.Change(alex, l => l with { Remember = false });
        Assert.Equal(new[] { alex, sam }.Order(), store.All().Select(l => l.AccountId).Order());
        Assert.False(store.Load(alex).Remember);
        store.Delete(alex);
        Assert.Equal([sam], store.All().Select(l => l.AccountId));
    }

    [Fact]
    public void A_damaged_file_starts_with_nothing_set()
    {
        var store = Store();
        store.SetPin(sam, "1234", null);
        File.WriteAllText(store.PathFor(sam), "{ not json");
        Assert.Empty(store.Load(sam).Methods);
    }

    public void Dispose()
    {
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class AccountVaultTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "martlet-account-vault-" + Guid.NewGuid().ToString("N"));

    public AccountVaultTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "memories"));
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"name\":\"Sam\"}");
        File.WriteAllBytes(Path.Combine(folder, "memories", "facts.db"), Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(folder, "empty.txt"), "");
    }

    [Fact]
    public void Locking_encrypts_every_file_and_unlocking_gives_them_back()
    {
        var key = AccountVault.NewKey();
        var sealedResult = AccountVault.Seal(folder, key);
        Assert.Equal(3, sealedResult.Done);
        Assert.Empty(sealedResult.Skipped);
        Assert.Equal((3, 0), AccountVault.Count(folder));
        Assert.False(File.Exists(Path.Combine(folder, "settings.json")));
        Assert.DoesNotContain("Sam", File.ReadAllText(Path.Combine(folder, "settings.json.mlock")));
        Assert.Equal(0, AccountVault.Seal(folder, key).Done);

        var opened = AccountVault.Unseal(folder, key);
        Assert.Equal(3, opened.Done);
        Assert.Equal((0, 3), AccountVault.Count(folder));
        Assert.Equal("{\"name\":\"Sam\"}", File.ReadAllText(Path.Combine(folder, "settings.json")));
        Assert.Equal(Enumerable.Range(0, 70_000).Select(i => (byte)i), File.ReadAllBytes(Path.Combine(folder, "memories", "facts.db")));
        Assert.Equal("", File.ReadAllText(Path.Combine(folder, "empty.txt")));
    }

    [Fact]
    public void Another_key_opens_nothing_and_leaves_the_files_encrypted()
    {
        AccountVault.Seal(folder, AccountVault.NewKey());
        var result = AccountVault.Unseal(folder, AccountVault.NewKey());
        Assert.Equal(0, result.Done);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Equal((3, 0), AccountVault.Count(folder));
    }

    [Fact]
    public void A_file_moved_to_another_name_does_not_open()
    {
        var key = AccountVault.NewKey();
        AccountVault.Seal(folder, key);
        File.Move(Path.Combine(folder, "settings.json.mlock"), Path.Combine(folder, "other.json.mlock"));
        Assert.Equal(["other.json"], AccountVault.Unseal(folder, key).Skipped);
    }

    [Fact]
    public void After_a_crash_the_plain_copy_wins()
    {
        var key = AccountVault.NewKey();
        AccountVault.Seal(folder, key);
        // A crash between the replace and the delete: both copies are there, and the plain one is current.
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"name\":\"Sam 2\"}");
        File.WriteAllText(Path.Combine(folder, "settings.json.mlockpart"), "half");
        AccountVault.Unseal(folder, key);
        Assert.Equal("{\"name\":\"Sam 2\"}", File.ReadAllText(Path.Combine(folder, "settings.json")));
        Assert.False(File.Exists(Path.Combine(folder, "settings.json.mlock")));
        Assert.False(File.Exists(Path.Combine(folder, "settings.json.mlockpart")));
        Assert.Equal((0, 3), AccountVault.Count(folder));
    }

    [Fact]
    public void A_file_in_use_stays_plain_and_is_reported()
    {
        var key = AccountVault.NewKey();
        using (new FileStream(Path.Combine(folder, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = AccountVault.Seal(folder, key);
            Assert.Equal(["settings.json"], result.Skipped);
            Assert.Equal(2, result.Done);
        }
        Assert.Equal((2, 1), AccountVault.Count(folder));
    }

    [Fact]
    public void A_wrapped_key_opens_only_with_its_secret()
    {
        var key = AccountVault.NewKey();
        var wrap = AccountVault.Wrap(key, "2468", 1_000);
        Assert.Equal(key, AccountVault.Unwrap(wrap, "2468"));
        Assert.Null(AccountVault.Unwrap(wrap, "1357"));
        Assert.Null(AccountVault.Unwrap(wrap with { Data = Convert.ToBase64String(new byte[48]) }, "2468"));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

public sealed class WindowsHelloTests
{
    [Fact]
    public void The_operation_interface_id_follows_the_windows_runtime_rule()
    {
        // IAsyncOperation<bool>'s well-known interface ID, from the Windows SDK.
        Assert.Equal(new Guid("cdb5efb3-5788-509d-9be1-71ccb8a3362a"), WindowsHello.Parameterized(new Guid("9fc2b0bb-e446-44e2-aa61-9cab8f636af2"), "b1"));
        Assert.True(WindowsHello.AvailabilityOperationHas(WindowsHello.AvailabilityOperation));
        Assert.False(WindowsHello.AvailabilityOperationHas(WindowsHello.VerificationOperation));
    }

    [Fact]
    public async Task Availability_answers_without_showing_anything()
    {
        var availability = await WindowsHello.AvailabilityAsync();
        Assert.True(Enum.IsDefined(availability));
        Assert.False(string.IsNullOrWhiteSpace(WindowsHello.Describe(availability)));
    }
}
