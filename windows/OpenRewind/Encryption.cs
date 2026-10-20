namespace OpenRewind;

/// Settings → Encryption & Lock → Encrypt Recordings: switching it on and off, the background
/// migration of what is already recorded, and the recovery key. Same as Encryption.swift on the Mac.
sealed class Encryption
{
    readonly Engine engine;
    readonly Settings settings;
    readonly SynchronizationContext? ui = SynchronizationContext.Current;
    CancellationTokenSource? cts;
    Task running = Task.CompletedTask;
    /// Files the last run could not convert (it tries again next launch).
    int failed;
    /// The database could not be converted in the last run (it tries again next launch).
    bool dbFailed;

    public Encryption(Engine engine, Settings settings)
    {
        this.engine = engine;
        this.settings = settings;
    }

    public bool On => settings.EncryptRecordings;

    /// At launch: finish a switch that was cut short (quit, crash, power cut).
    public void Resume()
    {
        if (!settings.EncryptionDone) Migrate(settings.EncryptRecordings);
    }

    /// The grey line under the switch. Read from what is on disk, not only from the setting.
    public string Status()
    {
        var verb = On ? "Encrypting" : "Decrypting";
        if (engine.Db.IsConverting) return $"{verb} the database… (recording waits until it is done)";
        if (VaultMigration.Running)
            return VaultMigration.Total == 0 ? $"{verb} the database…" : $"{verb} recordings… {VaultMigration.Done:N0} of {VaultMigration.Total:N0}";
        if (dbFailed) return $"The database could not be {(On ? "encrypted" : "decrypted")}; trying again next launch";
        if (failed > 0) return $"{failed:N0} files could not be {(On ? "encrypted" : "decrypted")}; trying again next launch";
        if (!settings.EncryptionDone || engine.Db.IsEncrypted != On) return $"{verb} recordings…";
        return On ? "Recordings and the database are encrypted" : "Recordings are stored as plain files";
    }

    /// Files that keep failing (damaged, or encrypted with another key) would be tried at every
    /// launch. The menu offers to stop: they stay as they are and the switch counts as done.
    public int Failed => failed;

    public void SkipFailed()
    {
        if (MessageBox.Show($"{failed:N0} recordings could not be {(On ? "encrypted" : "decrypted")}; they may be damaged or encrypted with another key. " +
                            "Stop trying? They stay as they are, and OpenRewind no longer tries them at every launch.",
                            "Stop retrying?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        Log.Write($"stopped retrying {failed} recordings that could not be converted");
        failed = 0;
        settings.EncryptionDone = true;
        settings.Save();
    }

    public void TurnOn()
    {
        if (On) return;
        if (MessageBox.Show(
                "OpenRewind will encrypt every new screenshot, audio clip and the database, and encrypt what is already recorded in the background. Recording keeps going.\n\n" +
                "The key is kept for your Windows account (DPAPI), so OpenRewind records after you sign in without asking for a password. " +
                "This protects your history if someone copies the disk, a backup or the .openrewind folder, or signs in as another user. It does not protect against programs running as you.\n\n" +
                "If your Windows account is reset or you move your recordings to another PC, you will need the recovery key. Without it, encrypted recordings are lost for good. Save it on the next screen and keep it somewhere safe.",
                "Encrypt your recordings?", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        var home = engine.Config.Home;
        var key = RecordingFile.Key;
        if (key == null)
        {
            var (stored, state) = DataKeyStore.Read(home);
            if (state == DataKeyStore.State.Unreadable)
            {
                // Never replace a key we can't read: files encrypted with it would be lost for good.
                MessageBox.Show("OpenRewind found its encryption key but can't unseal it (has your Windows password been reset?). Nothing was changed.\n\n" +
                                "Restart OpenRewind and enter your recovery key when it asks, then try again.",
                                "Encrypt Recordings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            key = stored;
        }
        if (key == null)
        {
            key = RecordingFile.NewKey();
            try { DataKeyStore.Save(home, key); }
            catch (Exception e)
            {
                Log.Write($"cannot save the encryption key: {e.Message}");
                MessageBox.Show($"OpenRewind could not save the encryption key ({e.Message}). Nothing was encrypted.", "Encrypt Recordings",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        RecordingFile.Key = key;
        RecordingFile.EncryptNew = true;
        settings.EncryptRecordings = true;
        settings.EncryptionDone = false;
        settings.Save();
        Log.Write("encrypt recordings: on");
        Migrate(true);
        if (!ExportKey())
            MessageBox.Show("Encryption is on. Save the recovery key soon: Settings → Encryption & Lock → Export Recovery Key…", "Encrypt Recordings",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public void TurnOff()
    {
        if (!On) return;
        if (MessageBox.Show("OpenRewind will decrypt your recordings and the database in the background and store new ones as plain files. " +
                            "Anyone who can read the .openrewind folder will be able to see them.",
                            "Stop encrypting recordings?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        RecordingFile.EncryptNew = false;
        settings.EncryptRecordings = false;
        settings.EncryptionDone = false;
        settings.Save();
        Log.Write("encrypt recordings: off");
        Migrate(false);
    }

    /// Convert in the background. A newer switch cancels the one before; each file is done on its own,
    /// so a switch that is cut short just leaves some files in each state, which both read fine.
    void Migrate(bool encrypt)
    {
        cts?.Cancel();
        var c = cts = new CancellationTokenSource();
        var before = running;
        failed = 0;
        dbFailed = false;
        running = Task.Run(async () =>
        {
            try { await before; } catch { }
            if (c.IsCancellationRequested) return;
            if (RecordingFile.Key is not byte[] key) { Log.Write("cannot convert recordings: the encryption key is not loaded"); return; }
            try
            {
                var r = VaultMigration.Run(engine.Db, engine.Config.DataDir, key, encrypt, c.Token);
                ui?.Post(_ =>
                {
                    failed = r.Failed;
                    dbFailed = r.DatabaseFailed;
                    if (r.Finished && settings.EncryptRecordings == encrypt) { settings.EncryptionDone = true; settings.Save(); }
                }, null);
            }
            catch (Exception e)
            {
                Log.Write($"converting recordings failed: {e.Message}");
                ui?.Post(_ => dbFailed = true, null);
            }
        });
    }

    public void Stop() => cts?.Cancel();

    public bool HasKey => RecordingFile.Key != null || DataKeyStore.Exists(engine.Config.Home);

    /// Save the recovery key as a text file. False when the user cancelled.
    public bool ExportKey()
    {
        var key = RecordingFile.Key ?? DataKeyStore.Load(engine.Config.Home);
        if (key == null) return false;
        using var dlg = new SaveFileDialog
        {
            Title = "Save Recovery Key", FileName = "OpenRewind Recovery Key.txt", Filter = "Text file (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return false;
        try
        {
            // Saved in the user's own folders, which only this Windows account can read by default.
            File.WriteAllText(dlg.FileName, RecoveryKey.File(key, DateTime.Now).Replace("\n", "\r\n"));
            Log.Write("recovery key saved");
            MessageBox.Show("Keep this file somewhere safe and private, like a password manager or a USB stick in a drawer. Anyone with it and a copy of your recordings can read them.",
                            "Recovery key saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not save the recovery key: {e.Message}", "Save Recovery Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// At launch, when the recordings are encrypted and this Windows account has no key for them:
    /// ask for the recovery key. Returns the key (checked against the database and saved), or null to quit.
    public static byte[]? AskForRecoveryKey(Config config)
    {
        while (true)
        {
            using var dlg = new RecoveryKeyForm();
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            var key = RecoveryKey.Parse(dlg.Entered);
            if (key == null)
            {
                MessageBox.Show("That is not a whole recovery key. It has 72 letters and digits in 9 groups; check for a missing or mistyped group.",
                                "OpenRewind", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            // Check it against the database, or an encrypted recording when the database is plain.
            var dbEncrypted = Database.IsEncryptedFile(config.DbPath);
            var sample = dbEncrypted ? null : RecordingFile.FirstEncrypted(config.DataDir);
            var opens = dbEncrypted ? Database.KeyOpens(config.DbPath, key) : sample == null || RecordingFile.KeyOpens(sample, key);
            if (!opens)
            {
                MessageBox.Show("This recovery key does not open these recordings. It may belong to another computer or an older key.",
                                "OpenRewind", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            // It opens the recordings, so it may replace a stored key that can't be unsealed (kept as a .bak).
            try { DataKeyStore.Save(config.Home, key, replace: true); } catch (Exception e) { Log.Write($"cannot save the encryption key: {e.Message}"); }
            Log.Write("encryption key restored from the recovery key");
            return key;
        }
    }
}

/// Paste the recovery key, or open the file Export Recovery Key… saved.
sealed class RecoveryKeyForm : Form
{
    readonly TextBox box;
    public string Entered => box.Text;

    public RecoveryKeyForm()
    {
        var k = DeviceDpi / 96f;
        Text = "OpenRewind: your recordings are encrypted";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size((int)(520 * k), (int)(260 * k));
        Font = new Font("Segoe UI", 9f);
        var info = new Label
        {
            Text = "Your recordings are encrypted, and the key is not on this Windows account (it may have been reset, " +
                   "or the recordings came from another PC). Paste the recovery key you saved, or open its file.\n\n" +
                   "Without it they can't be opened. To start over with an empty history, quit and move the .openrewind folder away.",
            Location = new Point((int)(16 * k), (int)(14 * k)), Size = new Size((int)(488 * k), (int)(100 * k)),
        };
        box = new TextBox { Location = new Point((int)(16 * k), (int)(120 * k)), Size = new Size((int)(488 * k), (int)(60 * k)), Multiline = true, Font = new Font("Consolas", 10f) };
        var open = new Button { Text = "Open File…", Location = new Point((int)(16 * k), (int)(212 * k)), AutoSize = true };
        open.Click += (_, _) =>
        {
            using var f = new OpenFileDialog { Title = "Open Recovery Key", Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*" };
            if (f.ShowDialog(this) == DialogResult.OK)
                try { box.Text = File.ReadAllText(f.FileName); } catch (Exception e) { MessageBox.Show(e.Message, "Open Recovery Key"); }
        };
        var ok = new Button { Text = "Unlock", DialogResult = DialogResult.OK, Location = new Point((int)(330 * k), (int)(212 * k)), AutoSize = true };
        var quit = new Button { Text = "Quit", DialogResult = DialogResult.Cancel, Location = new Point((int)(420 * k), (int)(212 * k)), AutoSize = true };
        AcceptButton = ok;
        CancelButton = quit;
        Controls.AddRange(new Control[] { info, box, open, ok, quit });
    }
}
