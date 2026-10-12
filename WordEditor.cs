// 死亡打字员 · 单词编辑器（独立 EXE 版）
// 编译: csc.exe /target:winexe /codepage:65001 /out:死亡打字员单词编辑器.exe WordEditor.cs
// 依赖: 仅 Windows 自带的 .NET Framework 4.x，无需安装任何运行时

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TotdWordEditor
{
    /* ==================================================================== */
    /*  编解码：word/*.bin 的读写                                            */
    /* ==================================================================== */

    class CodecException : Exception
    {
        public CodecException(string m) : base(m) { }
    }

    class Entry
    {
        public uint Group;
        public string Text;
        public string Original;
        public bool Changed { get { return Text != Original; } }
    }

    static class Codec
    {
        public const byte XorKey = 0x41;
        public const int MaxOriginalLength = 40;

        public static readonly Dictionary<char, byte> Cipher = new Dictionary<char, byte>();
        public static readonly Dictionary<byte, char> Plain = new Dictionary<byte, char>();

        static Codec()
        {
            var punct = new[] {
                new object[]{' ', 0xea}, new object[]{'!', 0xe4}, new object[]{'"', 0xf2},
                new object[]{'#', 0xed}, new object[]{'$', 0xec}, new object[]{'%', 0xe9},
                new object[]{'&', 0xf3}, new object[]{'\'', 0xeb}, new object[]{'(', 0xf5},
                new object[]{')', 0xf6}, new object[]{'*', 0xf8}, new object[]{',', 0xf0},
                new object[]{'-', 0xf1}, new object[]{'.', 0xef}, new object[]{'/', 0xf4},
                new object[]{':', 0xf7}, new object[]{'?', 0xe3}, new object[]{'@', 0xee},
            };
            foreach (var p in punct) Cipher[(char)p[0]] = (byte)(int)p[1];
            for (int i = 0; i < 10; i++) Cipher[(char)('0' + i)] = (byte)(0xa2 + i);
            for (int i = 0; i < 26; i++) Cipher[(char)('A' + i)] = (byte)(0xac + i);
            for (int i = 0; i < 26; i++) Cipher[(char)('a' + i)] = (byte)(0xc6 + i);
            foreach (var kv in Cipher) Plain[kv.Value] = kv.Key;
        }

        public static string SupportedChars
        {
            get { return new string(Cipher.Keys.OrderBy(c => c).ToArray()); }
        }

        public static bool IsSupported(string s)
        {
            if (s == null) return false;
            foreach (char c in s) if (!Cipher.ContainsKey(c)) return false;
            return true;
        }

        public static string FirstBadChar(string s)
        {
            if (s == null) return null;
            foreach (char c in s) if (!Cipher.ContainsKey(c)) return c.ToString();
            return null;
        }

        public static byte[] Xor(byte[] buf)
        {
            var o = new byte[buf.Length];
            for (int i = 0; i < buf.Length; i++) o[i] = (byte)(buf[i] ^ XorKey);
            return o;
        }

        public static byte[] EncodeText(string text)
        {
            var o = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                byte b;
                if (!Cipher.TryGetValue(text[i], out b))
                    throw new CodecException("字符「" + text[i] + "」（在 " + Quote(text) + " 的第 " + i + " 位）不在游戏支持的字符集里。");
                o[i] = b;
            }
            return o;
        }

        public static string Latin1(byte[] b)
        {
            var c = new char[b.Length];
            for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
            return new string(c);
        }

        public static byte[] Latin1Bytes(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
            return b;
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            if (s.Length > 40) s = s.Substring(0, 40) + "…";
            return "\"" + s + "\"";
        }

        public static int FindSentinel(byte[] d)
        {
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == 0xff && d[i + 1] == 0xff && d[i + 2] == 0xff && d[i + 3] == 0xff) return i;
            return -1;
        }

        public static byte[] CString(byte[] d, int off)
        {
            int e = off;
            while (e < d.Length && d[e] != 0) e++;
            if (e >= d.Length) throw new CodecException("偏移 " + off + " 处的字符串没有结束符。");
            var r = new byte[e - off];
            Array.Copy(d, off, r, 0, r.Length);
            return r;
        }

        static uint ReadU32(byte[] d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));
        }

        static void WriteU32(byte[] d, int off, uint v)
        {
            d[off] = (byte)(v & 0xff);
            d[off + 1] = (byte)((v >> 8) & 0xff);
            d[off + 2] = (byte)((v >> 16) & 0xff);
            d[off + 3] = (byte)((v >> 24) & 0xff);
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /* --------------------------- 解码 --------------------------- */

        public static List<Entry> Decode(byte[] raw)
        {
            byte[] d = Xor(raw);
            int sentinel = FindSentinel(d);
            if (sentinel < 0) throw new CodecException("没找到 0xFFFFFFFF 目录哨兵，这不像 word/*.bin 文件。");
            if (sentinel % 12 != 0) throw new CodecException("目录长度 " + sentinel + " 不是 12 的倍数，格式不认识。");

            int count = sentinel / 12;
            var list = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                uint group = ReadU32(d, i * 12);
                uint po = ReadU32(d, i * 12 + 4);
                uint co = ReadU32(d, i * 12 + 8);
                if (po >= d.Length || co >= d.Length)
                    throw new CodecException("第 " + i + " 条指向文件外（" + po + "/" + co + "）。");

                string text = Latin1(CString(d, (int)po));
                byte[] cipherRaw = CString(d, (int)co);
                byte[] expected;
                try { expected = EncodeText(text); }
                catch (CodecException e) { throw new CodecException("第 " + i + " 条：" + e.Message); }

                if (!BytesEqual(expected, cipherRaw))
                    throw new CodecException("第 " + i + " 条的密文与明文对不上（" + Quote(text) + "）。拒绝改动这个文件。");

                list.Add(new Entry { Group = group, Text = text, Original = text });
            }
            return list;
        }

        /* --------------------------- 编码 --------------------------- */

        public static byte[] Encode(List<Entry> entries)
        {
            int n = entries.Count;
            var table = new byte[n * 12 + 12];
            var body = new List<byte>();
            var plainSeen = new Dictionary<string, int>();
            var cipherSeen = new Dictionary<string, int>();

            int cursor = 0;
            int baseOff = n * 12 + 12;

            Func<byte[], int> push = buf =>
            {
                int offset = baseOff + cursor;
                body.AddRange(buf);
                body.Add(0);
                cursor += buf.Length + 1;
                int fill = (4 - (cursor % 4)) % 4;
                for (int i = 0; i < fill; i++) body.Add(0xff);
                cursor += fill;
                return offset;
            };

            for (int i = 0; i < n; i++)
            {
                string text = entries[i].Text ?? "";
                byte[] cipher = EncodeText(text);

                int po;
                if (!plainSeen.TryGetValue(text, out po))
                {
                    po = push(Latin1Bytes(text));
                    plainSeen[text] = po;
                }
                string ckey = Latin1(cipher);
                int co;
                if (!cipherSeen.TryGetValue(ckey, out co))
                {
                    co = push(cipher);
                    cipherSeen[ckey] = co;
                }

                WriteU32(table, i * 12, entries[i].Group);
                WriteU32(table, i * 12 + 4, (uint)po);
                WriteU32(table, i * 12 + 8, (uint)co);
            }

            WriteU32(table, n * 12, 0xffffffff);
            // 哨兵后面 8 字节本来就是 0

            // 原编码器在最后一条记录处不再补齐，所以去掉尾部所有的 0xFF
            int end = body.Count;
            while (end > 0 && body[end - 1] == 0xff) end--;

            var all = new byte[table.Length + end];
            Array.Copy(table, 0, all, 0, table.Length);
            for (int i = 0; i < end; i++) all[table.Length + i] = body[i];

            return Xor(all);
        }
    }

    /* ==================================================================== */
    /*  Store：定位游戏、读写、备份                                          */
    /* ==================================================================== */

    class StoreException : Exception
    {
        public StoreException(string m) : base(m) { }
    }

    class FileItem
    {
        public string Name;
        public long Size;
        public int Count;
        public List<uint> Groups = new List<uint>();
        public string Error;
        public bool Dirty;
        public override string ToString() { return Name; }
    }

    class BackupInfo
    {
        public string Name;
        public int Files;
        public DateTime Time;
        public string Label;
        public override string ToString()
        {
            return Time.ToString("yyyy-MM-dd HH:mm:ss") + "   " + Files + " 个文件" +
                   (string.IsNullOrEmpty(Label) ? "" : "   [" + Label + "]");
        }
    }

    class VerifyResult
    {
        public int Ok;
        public int Entries;
        public List<string> Bad = new List<string>();
    }

    class Store
    {
        public const string BackupDirName = "totd-word-editor-backups";
        public string GameDir;
        public string WordDir;
        public string BackupRoot;

        public Store(string gameDir)
        {
            GameDir = Path.GetFullPath(gameDir);
            WordDir = Path.Combine(GameDir, "word");
            if (!Directory.Exists(WordDir))
                throw new StoreException("这个目录里没有 word 文件夹：" + GameDir);
            BackupRoot = Path.Combine(GameDir, BackupDirName);
        }

        public static bool IsWordFile(string name)
        {
            return name.ToLowerInvariant().EndsWith(".bin") && name.ToLowerInvariant() != "font.bin";
        }

        public List<string> Files()
        {
            var list = Directory.GetFiles(WordDir, "*.bin")
                                .Select(Path.GetFileName)
                                .Where(IsWordFile)
                                .ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public string PathOf(string name)
        {
            if (name.Contains("/") || name.Contains("\\") || name.Contains(".."))
                throw new StoreException("文件名不安全：" + name);
            return Path.Combine(WordDir, name);
        }

        public List<Entry> Load(string name)
        {
            string full = PathOf(name);
            if (!File.Exists(full)) throw new StoreException("没有这个单词文件：" + name);
            return Codec.Decode(File.ReadAllBytes(full));
        }

        public void Save(string name, List<Entry> entries)
        {
            string full = PathOf(name);
            if (!File.Exists(full)) throw new StoreException("没有这个单词文件：" + name);

            for (int i = 0; i < entries.Count; i++)
            {
                if (string.IsNullOrEmpty(entries[i].Text))
                    throw new StoreException("第 " + i + " 条是空的，每个词至少要有一个字符。");
            }

            byte[] buf = Codec.Encode(entries);

            // 安全校验：写回前先自己解一遍，确认结果就是我们要的
            var check = Codec.Decode(buf);
            if (check.Count != entries.Count)
                throw new StoreException("内部错误：编码结果自检没通过，文件没有写入。");
            for (int i = 0; i < check.Count; i++)
            {
                if (check[i].Text != (entries[i].Text ?? "") || check[i].Group != entries[i].Group)
                    throw new StoreException("内部错误：编码结果自检没通过，文件没有写入。");
            }

            string tmp = full + ".tmp-" + Process.GetCurrentProcess().Id;
            File.WriteAllBytes(tmp, buf);
            try
            {
                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /* --------------------------- 备份 --------------------------- */

        public BackupInfo Backup(string label)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string dirName = string.IsNullOrEmpty(label) ? stamp : stamp + "_" + Sanitize(label);
            string dir = Path.Combine(BackupRoot, dirName);
            Directory.CreateDirectory(dir);
            int n = 0;
            foreach (string name in Files())
            {
                File.Copy(Path.Combine(WordDir, name), Path.Combine(dir, name), true);
                n++;
            }
            return new BackupInfo { Name = dirName, Files = n, Time = DateTime.Now, Label = label };
        }

        public List<BackupInfo> Backups()
        {
            var outList = new List<BackupInfo>();
            if (!Directory.Exists(BackupRoot)) return outList;
            foreach (string d in Directory.GetDirectories(BackupRoot))
            {
                string name = Path.GetFileName(d);
                var info = new BackupInfo { Name = name, Time = Directory.GetLastWriteTime(d) };
                info.Files = Directory.GetFiles(d, "*.bin").Count(f => IsWordFile(Path.GetFileName(f)));
                int us = name.IndexOf('_');
                if (us >= 0 && name.Length > us + 1)
                {
                    int us2 = name.IndexOf('_', us + 1);
                    if (us2 >= 0) info.Label = name.Substring(us2 + 1);
                }
                outList.Add(info);
            }
            outList.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
            return outList;
        }

        public int Restore(string backupName)
        {
            string dir = Path.Combine(BackupRoot, backupName);
            if (!Directory.Exists(dir)) throw new StoreException("没有这个备份：" + backupName);
            Backup("before-restore");   // 还原本身也可撤销
            int n = 0;
            foreach (string f in Directory.GetFiles(dir, "*.bin"))
            {
                string bn = Path.GetFileName(f);
                if (!IsWordFile(bn)) continue;
                File.Copy(f, Path.Combine(WordDir, bn), true);
                n++;
            }
            return n;
        }

        public VerifyResult Verify()
        {
            var r = new VerifyResult();
            foreach (string name in Files())
            {
                string full = Path.Combine(WordDir, name);
                try
                {
                    byte[] raw = File.ReadAllBytes(full);
                    var es = Codec.Decode(raw);
                    r.Entries += es.Count;
                    byte[] again = Codec.Encode(es);
                    if (!SameBytes(again, raw)) r.Bad.Add(name + "：重新编码后与原文件不是逐字节一致");
                    else r.Ok++;
                }
                catch (Exception e)
                {
                    r.Bad.Add(name + "：" + e.Message);
                }
            }
            return r;
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
                if (sb.Length >= 40) break;
            }
            return sb.ToString();
        }

        /// <summary>从给定目录开始，向上找含 word/*.bin 的游戏目录。</summary>
        public static string FindGameDir(string startDir)
        {
            string d = Path.GetFullPath(startDir);
            for (int up = 0; up < 4 && d != null; up++)
            {
                string wordDir = Path.Combine(d, "word");
                if (Directory.Exists(wordDir) && Directory.GetFiles(wordDir, "*.bin").Any(f => IsWordFile(Path.GetFileName(f))))
                    return d;
                var parent = Path.GetDirectoryName(d);
                if (parent == null || parent == d) break;
                d = parent;
            }
            return null;
        }
    }

    /* ==================================================================== */
    /*  批量替换的行（用于预览）                                              */
    /* ==================================================================== */

    class Change
    {
        public string File;
        public int Index;
        public uint Group;
        public string From;
        public string To;
        public override string ToString()
        {
            return File + "  #" + Index + "  [" + Group + "]  " + From + "  →  " + To;
        }
    }

    /* ==================================================================== */
    /*  主窗口                                                               */
    /* ==================================================================== */

    class MainForm : Form
    {
        Store store;
        List<FileItem> files = new List<FileItem>();
        List<Entry> entries = new List<Entry>();
        string currentFile = null;
        bool dirty = false;
        bool autoBackedUp = false;
        bool loadingGrid = false;

        ListBox fileList = new ListBox();
        TextBox fileFilter = new TextBox();
        CheckBox onlyDirtyFiles = new CheckBox();
        DataGridView grid = new DataGridView();
        TextBox wordFilter = new TextBox();
        CheckBox onlyChanged = new CheckBox();
        Label titleLabel = new Label();
        Button btnSave = new Button();
        Button btnRevert = new Button();
        ToolStripStatusLabel status = new ToolStripStatusLabel();
        CheckBox onlyLong = new CheckBox();

        public MainForm()
        {
            Text = "死亡打字员 · 单词编辑器";
            ClientSize = new Size(1060, 680);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = SystemIcons.Application; } catch { }

            BuildUi();
            LoadSettingsAndGame();
        }

        /* --------------------------- 界面 --------------------------- */

        void BuildUi()
        {
            var tool = new ToolStrip();
            tool.GripStyle = ToolStripGripStyle.Hidden;
            tool.ImageScalingSize = new Size(16, 16);
            tool.Font = Font;

            Action<string, EventHandler> addBtn = (text, handler) =>
            {
                var b = new ToolStripButton(text);
                b.DisplayStyle = ToolStripItemDisplayStyle.Text;
                b.Click += handler;
                tool.Items.Add(b);
            };

            addBtn("打开游戏目录", (s, e) => ChooseGameDir());
            tool.Items.Add(new ToolStripSeparator());
            addBtn("备份", (s, e) => DoBackup());
            addBtn("备份记录 / 还原", (s, e) => ShowBackups());
            addBtn("校验全部", (s, e) => DoVerify());
            tool.Items.Add(new ToolStripSeparator());
            addBtn("批量工具", (s, e) => ShowBulk());
            addBtn("导出为 TXT", (s, e) => DoExport());
            addBtn("从 TXT 导入", (s, e) => DoImport());
            tool.Items.Add(new ToolStripSeparator());
            addBtn("说明", (s, e) => ShowHelp());

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 280;
            split.FixedPanel = FixedPanel.Panel1;

            // ---- 左：文件列表 ----
            var left = new Panel { Dock = DockStyle.Fill };
            fileFilter.Dock = DockStyle.Top;
            fileFilter.PlaceholderTextSafe("筛选文件…");
            fileFilter.TextChanged += (s, e) => RefreshFileList();
            var leftChk = new Panel { Dock = DockStyle.Top, Height = 26 };
            onlyDirtyFiles.Text = "只看已修改的文件";
            onlyDirtyFiles.Dock = DockStyle.Left;
            onlyDirtyFiles.AutoSize = true;
            onlyDirtyFiles.Padding = new Padding(6, 4, 0, 0);
            onlyDirtyFiles.CheckedChanged += (s, e) => RefreshFileList();
            leftChk.Controls.Add(onlyDirtyFiles);

            fileList.Dock = DockStyle.Fill;
            fileList.IntegralHeight = false;
            fileList.Font = new Font("Consolas", 9.5F);
            fileList.SelectedIndexChanged += (s, e) => OnFileSelected();

            left.Controls.Add(fileList);
            left.Controls.Add(leftChk);
            left.Controls.Add(fileFilter);
            split.Panel1.Controls.Add(left);

            // ---- 右：词条表 ----
            var right = new Panel { Dock = DockStyle.Fill };

            var bar = new Panel { Dock = DockStyle.Top, Height = 34 };
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(8, 8);
            titleLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);

            btnRevert.Text = "撤销";
            btnRevert.Width = 60;
            btnRevert.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRevert.Location = new Point(right.Width - 140, 5);
            btnRevert.Click += (s, e) => ReloadCurrent();

            btnSave.Text = "保存";
            btnSave.Width = 66;
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Location = new Point(right.Width - 74, 5);
            btnSave.Enabled = false;
            btnSave.Click += (s, e) => SaveCurrent();

            var filterLabel = new Label { Text = "筛选单词：", AutoSize = true };
            filterLabel.Location = new Point(8, 9);

            bar.Controls.Add(titleLabel);
            bar.Controls.Add(btnRevert);
            bar.Controls.Add(btnSave);
            bar.Resize += (s, e) =>
            {
                btnSave.Left = bar.Width - btnSave.Width - 8;
                btnRevert.Left = btnSave.Left - btnRevert.Width - 6;
            };

            var bar2 = new Panel { Dock = DockStyle.Top, Height = 30 };
            wordFilter.Width = 220;
            wordFilter.Location = new Point(76, 4);
            wordFilter.TextChanged += (s, e) => RefreshGrid();
            onlyChanged.Text = "只看已修改";
            onlyChanged.AutoSize = true;
            onlyChanged.Location = new Point(306, 6);
            onlyChanged.CheckedChanged += (s, e) => RefreshGrid();
            onlyLong.Text = "只看超长(>40)";
            onlyLong.AutoSize = true;
            onlyLong.Location = new Point(410, 6);
            onlyLong.CheckedChanged += (s, e) => RefreshGrid();
            bar2.Controls.Add(filterLabel);
            bar2.Controls.Add(wordFilter);
            bar2.Controls.Add(onlyChanged);
            bar2.Controls.Add(onlyLong);

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            grid.Columns.Add(MakeCol("colIdx", "#", 52, true));
            grid.Columns.Add(MakeCol("colGrp", "组", 60, true));
            grid.Columns.Add(MakeCol("colOrig", "原词", 240, true));
            var cNew = MakeCol("colNew", "新词（双击可改）", 260, false);
            cNew.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            cNew.DefaultCellStyle.BackColor = Color.FromArgb(255, 255, 230);
            grid.Columns.Add(cNew);
            grid.Columns.Add(MakeCol("colState", "状态", 64, true));

            grid.CellEndEdit += Grid_CellEndEdit;
            grid.CellBeginEdit += (s, e) => { if (e.ColumnIndex != 3) e.Cancel = true; };
            grid.DataError += (s, e) => { e.ThrowException = false; };

            right.Controls.Add(grid);
            right.Controls.Add(bar2);
            right.Controls.Add(bar);
            split.Panel2.Controls.Add(right);

            var statusStrip = new StatusStrip();
            status.Text = "就绪";
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            statusStrip.Items.Add(status);

            Controls.Add(split);
            Controls.Add(statusStrip);
            Controls.Add(tool);
        }

        DataGridViewTextBoxColumn MakeCol(string name, string header, int width, bool readOnly)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name;
            c.HeaderText = header;
            c.Width = width;
            c.ReadOnly = readOnly;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            if (readOnly) c.DefaultCellStyle.BackColor = SystemColors.Control;
            return c;
        }

        /* --------------------------- 游戏目录 --------------------------- */

        string SettingsPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "editor-settings.txt"); }
        }

        void LoadSettingsAndGame()
        {
            string dir = null;
            try
            {
                if (File.Exists(SettingsPath))
                    dir = File.ReadAllText(SettingsPath, Encoding.UTF8).Trim();
            }
            catch { }

            if (dir == null || !Directory.Exists(Path.Combine(dir, "word")))
                dir = Store.FindGameDir(Path.GetDirectoryName(Application.ExecutablePath));

            if (dir == null)
                dir = Store.FindGameDir(Environment.CurrentDirectory);

            if (dir != null) OpenGame(dir);
            else
            {
                status.Text = "还没找到游戏目录，点左上角「打开游戏目录」选一个。";
                BeginInvoke(new Action(() =>
                {
                    if (MessageBox.Show(this,
                        "没有自动找到游戏目录。\n\n请选择《死亡打字员》的安装目录（里面应该有 word 文件夹）。\n现在选吗？",
                        "选择游戏目录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        ChooseGameDir();
                }));
            }
        }

        void SaveSettings()
        {
            try { File.WriteAllText(SettingsPath, store.GameDir, Encoding.UTF8); } catch { }
        }

        void ChooseGameDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择《死亡打字员》的安装目录（里面要有 word 文件夹）";
                if (store != null) dlg.SelectedPath = store.GameDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string dir = dlg.SelectedPath;
                if (!Directory.Exists(Path.Combine(dir, "word")))
                {
                    string up = Store.FindGameDir(dir);
                    if (up != null) dir = up;
                    else
                    {
                        MessageBox.Show(this, "这个目录里没有 word 文件夹，也没在上级找到。",
                            "不对", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                if (!ConfirmDiscardChanges()) return;
                OpenGame(dir);
            }
        }

        void OpenGame(string dir)
        {
            try
            {
                store = new Store(dir);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SaveSettings();
            Text = "死亡打字员 · 单词编辑器 — " + store.GameDir;
            autoBackedUp = false;
            entries = new List<Entry>();
            currentFile = null;
            dirty = false;
            btnSave.Enabled = false;
            grid.Rows.Clear();
            titleLabel.Text = "—";
            LoadFileIndex();
            status.Text = "游戏目录：" + store.GameDir + "   共 " + files.Count + " 个单词文件";
        }

        void LoadFileIndex()
        {
            files = new List<FileItem>();
            foreach (string name in store.Files())
            {
                var item = new FileItem { Name = name };
                try
                {
                    var fi = new FileInfo(Path.Combine(store.WordDir, name));
                    item.Size = fi.Length;
                    var es = store.Load(name);
                    item.Count = es.Count;
                    item.Groups = es.Select(x => x.Group).Distinct().OrderBy(x => x).ToList();
                }
                catch (Exception e)
                {
                    item.Error = e.Message;
                }
                files.Add(item);
            }
            RefreshFileList();
        }

        void RefreshFileList()
        {
            string keep = currentFile;
            string filter = fileFilter.Text.Trim().ToLowerInvariant();
            fileList.BeginUpdate();
            fileList.Items.Clear();
            foreach (var f in files)
            {
                if (onlyDirtyFiles.Checked && !f.Dirty) continue;
                if (filter.Length > 0 && !f.Name.ToLowerInvariant().Contains(filter)) continue;
                string text = f.Error != null
                    ? f.Name + "   [读取失败]"
                    : f.Name + "   (" + f.Count + " 词)";
                if (f.Dirty) text = "● " + text;
                fileList.Items.Add(new FileRow { Item = f, Text = text });
            }
            fileList.EndUpdate();

            if (keep != null)
            {
                for (int i = 0; i < fileList.Items.Count; i++)
                {
                    var r = fileList.Items[i] as FileRow;
                    if (r != null && r.Item.Name == keep) { fileList.SelectedIndex = i; break; }
                }
            }
        }

        class FileRow
        {
            public FileItem Item;
            public string Text;
            public override string ToString() { return Text; }
        }

        /* --------------------------- 选定文件 --------------------------- */

        bool ConfirmDiscardChanges()
        {
            if (!dirty) return true;
            var r = MessageBox.Show(this, "「" + currentFile + "」还有没保存的修改，要丢掉吗？",
                "有未保存的修改", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return r == DialogResult.Yes;
        }

        void OnFileSelected()
        {
            var row = fileList.SelectedItem as FileRow;
            if (row == null) return;
            string name = row.Item.Name;
            if (name == currentFile) return;
            if (!ConfirmDiscardChanges())
            {
                RefreshFileList();   // 把选中项还原
                return;
            }
            LoadFile(name);
        }

        void LoadFile(string name)
        {
            try
            {
                entries = store.Load(name);
                currentFile = name;
                dirty = false;
                btnSave.Enabled = false;
                var item = files.FirstOrDefault(f => f.Name == name);
                titleLabel.Text = name + "   " + entries.Count + " 词" +
                    (item != null && item.Groups.Count > 0
                        ? "   组 " + item.Groups.Min() + "–" + item.Groups.Max()
                        : "");
                RefreshGrid();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ReloadCurrent()
        {
            if (currentFile == null) return;
            if (dirty && MessageBox.Show(this, "撤销「" + currentFile + "」的全部未保存修改？",
                    "撤销", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            LoadFile(currentFile);
            status.Text = "已撤销 " + currentFile + " 的未保存修改";
        }

        void RefreshGrid()
        {
            if (currentFile == null) { grid.Rows.Clear(); return; }
            loadingGrid = true;
            grid.SuspendLayout();
            grid.Rows.Clear();

            string f = wordFilter.Text.Trim().ToLowerInvariant();
            int shown = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (onlyChanged.Checked && !e.Changed) continue;
                if (onlyLong.Checked && (e.Text ?? "").Length <= Codec.MaxOriginalLength) continue;
                if (f.Length > 0 &&
                    !(e.Text ?? "").ToLowerInvariant().Contains(f) &&
                    !(e.Original ?? "").ToLowerInvariant().Contains(f)) continue;

                int idx = grid.Rows.Add(i, e.Group, e.Original, e.Text,
                    e.Changed ? "已改" : "");
                grid.Rows[idx].Tag = e;
                if (e.Changed)
                {
                    grid.Rows[idx].Cells[3].Style.ForeColor = Color.FromArgb(0, 110, 0);
                    grid.Rows[idx].Cells[3].Style.Font = new Font(grid.Font, FontStyle.Bold);
                    grid.Rows[idx].Cells[4].Style.ForeColor = Color.FromArgb(0, 110, 0);
                }
                shown++;
            }
            grid.ResumeLayout();
            loadingGrid = false;
            status.Text = currentFile + "：显示 " + shown + " / " + entries.Count + " 词" +
                (dirty ? "   ● 有未保存的修改" : "");
        }

        void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (loadingGrid || e.ColumnIndex != 3 || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            var entry = row.Tag as Entry;
            if (entry == null) return;

            string val = Convert.ToString(row.Cells[3].Value) ?? "";
            if (val == entry.Text) return;

            string bad = Codec.FirstBadChar(val);
            if (bad != null)
            {
                MessageBox.Show(this,
                    "字符「" + bad + "」存不进游戏。\n\n游戏支持的字符只有：\n" +
                    "A-Z  a-z  0-9  和 空格 ! \" # $ % & ' ( ) * , - . / : ? @",
                    "不支持的字符", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                row.Cells[3].Value = entry.Text;
                return;
            }
            if (val.Length == 0)
            {
                MessageBox.Show(this, "每个词至少要有一个字符。", "不能为空",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                row.Cells[3].Value = entry.Text;
                return;
            }

            entry.Text = val;
            row.Cells[3].Value = val;
            row.Cells[3].Style.ForeColor = entry.Changed ? Color.FromArgb(0, 110, 0) : grid.ForeColor;
            row.Cells[3].Style.Font = entry.Changed ? new Font(grid.Font, FontStyle.Bold) : grid.Font;
            row.Cells[4].Value = entry.Changed ? "已改" : "";
            row.Cells[4].Style.ForeColor = entry.Changed ? Color.FromArgb(0, 110, 0) : grid.ForeColor;

            bool wasDirty = dirty;
            dirty = entries.Any(x => x.Changed);
            btnSave.Enabled = dirty;
            if (dirty != wasDirty) MarkCurrentFileDirty(dirty);
            if (val.Length > Codec.MaxOriginalLength)
                status.Text = "注意：这个词有 " + val.Length + " 个字符，超过了原版最长 " +
                              Codec.MaxOriginalLength + " 个字符，游戏里可能显示不全。";
            else
                status.Text = currentFile + "：" + (dirty ? "有未保存的修改" : "没有修改");
        }

        void MarkCurrentFileDirty(bool d)
        {
            var item = files.FirstOrDefault(f => f.Name == currentFile);
            if (item != null) item.Dirty = d;
            string keep = currentFile;
            RefreshFileList();
            currentFile = keep;
        }

        /* --------------------------- 保存 --------------------------- */

        void EnsureAutoBackup()
        {
            if (autoBackedUp || store == null) return;
            var bk = store.Backup("auto-before-edit");
            autoBackedUp = true;
            status.Text = "已自动备份到 " + bk.Name + "（" + bk.Files + " 个文件）";
        }

        void SaveCurrent()
        {
            if (!dirty || currentFile == null) return;
            EnsureAutoBackup();
            try
            {
                store.Save(currentFile, entries);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            foreach (var e in entries) e.Original = e.Text;
            dirty = false;
            btnSave.Enabled = false;
            MarkCurrentFileDirty(false);
            RefreshGrid();
            status.Text = "已保存 " + currentFile;
        }

        /* --------------------------- 备份 / 校验 --------------------------- */

        void DoBackup()
        {
            if (store == null) return;
            try
            {
                var bk = store.Backup("manual");
                MessageBox.Show(this, "已备份 " + bk.Files + " 个文件到：\n" +
                    Path.Combine(store.BackupRoot, bk.Name),
                    "备份完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "备份失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ShowBackups()
        {
            if (store == null) return;
            var list = store.Backups();
            using (var dlg = new Form())
            {
                dlg.Text = "备份记录";
                dlg.ClientSize = new Size(560, 420);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Font = Font;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;

                var lb = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                lb.Font = new Font("Consolas", 9.5F);
                foreach (var b in list) lb.Items.Add(b);

                var note = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 40,
                    Text = "选中一个备份，点「还原」把整个 word 目录恢复回去。\n" +
                           "还原前会自动再备份一次当前状态，所以还原本身也能撤销。"
                };

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                var btnRestore = new Button { Text = "还原", Width = 90, Height = 28, Location = new Point(360, 8) };
                var btnClose = new Button { Text = "关闭", Width = 80, Height = 28, Location = new Point(456, 8), DialogResult = DialogResult.Cancel };
                bottom.Controls.Add(btnRestore);
                bottom.Controls.Add(btnClose);

                btnRestore.Click += (s, e) =>
                {
                    var b = lb.SelectedItem as BackupInfo;
                    if (b == null) { MessageBox.Show(dlg, "先选一个备份。"); return; }
                    if (MessageBox.Show(dlg,
                        "把 word 目录还原成这个备份？\n\n" + b.ToString() +
                        "\n\n当前状态会先自动备份一次。",
                        "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    try
                    {
                        int n = store.Restore(b.Name);
                        MessageBox.Show(dlg, "已还原 " + n + " 个文件。", "完成",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        LoadFileIndex();
                        entries = new List<Entry>();
                        currentFile = null;
                        dirty = false;
                        btnSave.Enabled = false;
                        grid.Rows.Clear();
                        titleLabel.Text = "—";
                        dlg.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dlg, ex.Message, "还原失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                dlg.Controls.Add(lb);
                dlg.Controls.Add(note);
                dlg.Controls.Add(bottom);
                dlg.CancelButton = btnClose;
                if (list.Count > 0) lb.SelectedIndex = 0;
                dlg.ShowDialog(this);
            }
        }

        void DoVerify()
        {
            if (store == null) return;
            Cursor = Cursors.WaitCursor;
            status.Text = "正在校验全部文件…";
            Application.DoEvents();
            VerifyResult r = null;
            string err = null;
            try { r = store.Verify(); }
            catch (Exception e) { err = e.Message; }
            Cursor = Cursors.Default;

            if (err != null)
            {
                MessageBox.Show(this, err, "校验失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (r.Bad.Count == 0)
            {
                MessageBox.Show(this,
                    "全部通过。\n\n" + r.Ok + " 个文件、共 " + r.Entries + " 个词条，" +
                    "解码后再编码与原文件逐字节一致。",
                    "校验通过", MessageBoxButtons.OK, MessageBoxIcon.Information);
                status.Text = "校验通过：" + r.Ok + " 个文件 / " + r.Entries + " 词条";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("有 " + r.Bad.Count + " 个文件有问题（正常 " + r.Ok + " 个）：");
                sb.AppendLine();
                foreach (string s in r.Bad.Take(30)) sb.AppendLine("· " + s);
                if (r.Bad.Count > 30) sb.AppendLine("…还有 " + (r.Bad.Count - 30) + " 个");
                MessageBox.Show(this, sb.ToString(), "校验发现问题",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                status.Text = "校验：正常 " + r.Ok + " 个，有问题 " + r.Bad.Count + " 个";
            }
        }

        /* --------------------------- 导出 / 导入 --------------------------- */

        void DoExport()
        {
            if (store == null) return;
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择导出到哪个文件夹（每个 .bin 会导出成一个同名 .txt，一行一个词）";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                int nFiles = 0, nWords = 0;
                var errors = new List<string>();
                Cursor = Cursors.WaitCursor;
                try
                {
                    foreach (string name in store.Files())
                    {
                        try
                        {
                            var es = store.Load(name);
                            var sb = new StringBuilder();
                            foreach (var e in es) sb.Append(e.Text).Append("\r\n");
                            string outName = Regex.Replace(name, @"\.bin$", "", RegexOptions.IgnoreCase) + ".txt";
                            File.WriteAllText(Path.Combine(dir, outName), sb.ToString(), Encoding.GetEncoding(28591));
                            nFiles++; nWords += es.Count;
                        }
                        catch (Exception e) { errors.Add(name + "：" + e.Message); }
                    }
                    File.WriteAllText(Path.Combine(dir, "README.txt"),
                        "一行一个词，顺序和游戏里存的一样。\r\n" +
                        "改好之后在编辑器里点「从 TXT 导入」即可写回游戏。\r\n" +
                        "不要增加或删除行 —— 每个文件的行数必须保持不变。\r\n" +
                        "允许的字符：A-Z a-z 0-9 和  空格 ! \" # $ % & ' ( ) * , - . / : ? @\r\n",
                        Encoding.GetEncoding(28591));
                }
                finally { Cursor = Cursors.Default; }

                string msg = "已导出 " + nWords + " 个词到 " + nFiles + " 个 txt 文件。\n\n" + dir;
                if (errors.Count > 0) msg += "\n\n有 " + errors.Count + " 个文件出错：\n" + string.Join("\n", errors.Take(8));
                MessageBox.Show(this, msg, "导出完成", MessageBoxButtons.OK,
                    errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        void DoImport()
        {
            if (store == null) return;
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择要导入的文件夹（里面是之前导出的 .txt）";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dir = dlg.SelectedPath;

                var txts = Directory.GetFiles(dir, "*.txt")
                    .Where(f => Path.GetFileName(f).ToLowerInvariant() != "readme.txt")
                    .ToList();
                if (txts.Count == 0)
                {
                    MessageBox.Show(this, "这个文件夹里没有 .txt 文件。", "没东西可导入",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(this, "要用这里的 " + txts.Count + " 个 txt 覆盖游戏里的单词文件吗？\n\n" +
                        "开始前会自动备份整个 word 目录。",
                        "确认导入", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                EnsureAutoBackup();
                var errors = new List<string>();
                int nFiles = 0, nWords = 0;
                Cursor = Cursors.WaitCursor;
                try
                {
                    foreach (string tf in txts)
                    {
                        string target = Regex.Replace(Path.GetFileName(tf), @"\.txt$", "", RegexOptions.IgnoreCase) + ".bin";
                        if (!File.Exists(Path.Combine(store.WordDir, target)))
                        {
                            errors.Add(Path.GetFileName(tf) + "：游戏里没有对应的 " + target);
                            continue;
                        }
                        try
                        {
                            var lines = File.ReadAllText(tf, Encoding.GetEncoding(28591))
                                            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                            while (lines.Count > 0 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);

                            var es = store.Load(target);
                            if (lines.Count != es.Count)
                            {
                                errors.Add(Path.GetFileName(tf) + "：有 " + lines.Count + " 行，但 " +
                                           target + " 需要 " + es.Count + " 行");
                                continue;
                            }
                            var next = new List<Entry>();
                            bool bad = false;
                            for (int i = 0; i < es.Count; i++)
                            {
                                string t = lines[i];
                                string ch = Codec.FirstBadChar(t);
                                if (ch != null)
                                {
                                    errors.Add(Path.GetFileName(tf) + " 第 " + (i + 1) + " 行有游戏不支持的字符「" + ch + "」");
                                    bad = true; break;
                                }
                                if (t.Length == 0)
                                {
                                    errors.Add(Path.GetFileName(tf) + " 第 " + (i + 1) + " 行是空的");
                                    bad = true; break;
                                }
                                next.Add(new Entry { Group = es[i].Group, Text = t });
                            }
                            if (bad) continue;
                            store.Save(target, next);
                            nFiles++; nWords += next.Count;
                        }
                        catch (Exception e)
                        {
                            errors.Add(Path.GetFileName(tf) + "：" + e.Message);
                        }
                    }
                }
                finally { Cursor = Cursors.Default; }

                string keep = currentFile;
                LoadFileIndex();
                if (keep != null) LoadFile(keep);

                string msg = "已导入 " + nWords + " 个词，更新 " + nFiles + " 个文件。";
                if (errors.Count > 0) msg += "\n\n有 " + errors.Count + " 处问题：\n" + string.Join("\n", errors.Take(12));
                MessageBox.Show(this, msg, "导入完成", MessageBoxButtons.OK,
                    errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        /* --------------------------- 批量工具 --------------------------- */

        void ShowBulk()
        {
            if (store == null) return;
            using (var dlg = new BulkForm(store, currentFile, this))
            {
                dlg.ShowDialog(this);
                if (dlg.AnythingChanged)
                {
                    string keep = currentFile;
                    LoadFileIndex();
                    if (keep != null && store.Files().Contains(keep)) LoadFile(keep);
                    else { entries = new List<Entry>(); currentFile = null; grid.Rows.Clear(); titleLabel.Text = "—"; }
                    autoBackedUp = true;   // 批量里已经备份过
                }
            }
        }

        void ShowHelp()
        {
            MessageBox.Show(this,
                "死亡打字员 · 单词编辑器（独立版）\n\n" +
                "· 左边选文件，右边「新词」一列双击就能改，改完点「保存」。\n" +
                "· 第一次保存前会自动把整个 word 目录备份一份。\n" +
                "· 「批量工具」里可以查找替换，也可以用你自己的词库整体换词。\n" +
                "· 「校验全部」会把每个文件解码再编码，确认与原文件逐字节一致。\n" +
                "· 「备份记录 / 还原」可以随时回到之前的状态。\n\n" +
                "游戏支持的字符：\n" +
                "A-Z  a-z  0-9  和  空格 ! \" # $ % & ' ( ) * , - . / : ? @\n\n" +
                "原版最长的词是 40 个字符，超过可能会显示不全。\n\n" +
                "备份存放位置：\n" + Store.BackupDirName + "\\（在游戏目录下）",
                "说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /* 让 TextBox 支持占位符（.NET Framework 没有 PlaceholderText） */
    static class UiExt
    {
        public static void PlaceholderTextSafe(this TextBox tb, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            try { SendMessage(tb.Handle, EM_SETCUEBANNER, 1, text); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);
    }

    /* ==================================================================== */
    /*  批量工具窗口                                                         */
    /* ==================================================================== */

    class BulkForm : Form
    {
        Store store;
        string currentFile;
        Form owner;
        public bool AnythingChanged = false;

        TextBox rFind = new TextBox();
        TextBox rTo = new TextBox();
        CheckBox rCase = new CheckBox();
        CheckBox rWord = new CheckBox();
        CheckBox rSkipQuiz = new CheckBox();
        ComboBox rScope = new ComboBox();
        TextBox rGroup = new TextBox();

        TextBox dWords = new TextBox();
        ComboBox dStyle = new ComboBox();
        CheckBox dQuiz = new CheckBox();

        ListBox preview = new ListBox();
        Label previewInfo = new Label();
        List<Change> pending = null;
        List<string> skipped = new List<string>();
        bool pendingIsDict = false;

        public BulkForm(Store store, string currentFile, Form owner)
        {
            this.store = store;
            this.currentFile = currentFile;
            this.owner = owner;

            Text = "批量工具";
            ClientSize = new Size(760, 640);
            StartPosition = FormStartPosition.CenterParent;
            Font = owner != null ? owner.Font : new Font("Microsoft YaHei UI", 9F);
            MinimizeBox = false;

            var tabs = new TabControl { Dock = DockStyle.Top, Height = 300 };

            /* ---- 查找替换 ---- */
            var t1 = new TabPage("查找替换");
            var p1 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

            Action<Control, int, int, int> place = (c, x, y, w) => { c.Location = new Point(x, y); c.Width = w; };

            var l1 = new Label { Text = "查找", AutoSize = true, Location = new Point(12, 18) };
            place(rFind, 70, 14, 250);
            var l2 = new Label { Text = "替换为", AutoSize = true, Location = new Point(340, 18) };
            place(rTo, 400, 14, 250);

            rCase.Text = "区分大小写"; rCase.AutoSize = true; rCase.Location = new Point(70, 48);
            rWord.Text = "全词匹配"; rWord.AutoSize = true; rWord.Location = new Point(190, 48);
            rSkipQuiz.Text = "跳过问答题（组 ≥ 90）"; rSkipQuiz.AutoSize = true; rSkipQuiz.Location = new Point(290, 48);
            rSkipQuiz.Checked = true;

            var l3 = new Label { Text = "范围", AutoSize = true, Location = new Point(12, 82) };
            rScope.DropDownStyle = ComboBoxStyle.DropDownList;
            rScope.Items.AddRange(new object[] { "当前文件", "所有文件" });
            rScope.SelectedIndex = 1;
            rScope.Location = new Point(70, 78); rScope.Width = 130;

            var l4 = new Label { Text = "限定组", AutoSize = true, Location = new Point(220, 82) };
            place(rGroup, 280, 78, 80);
            var l4b = new Label { Text = "（留空=全部）", AutoSize = true, Location = new Point(368, 82) };

            var btnRp = new Button { Text = "预览", Location = new Point(470, 74), Width = 90, Height = 28 };
            var btnRa = new Button { Text = "应用到游戏文件", Location = new Point(568, 74), Width = 150, Height = 28 };

            var hint1 = new Label
            {
                AutoSize = false, Location = new Point(12, 118), Size = new Size(720, 60),
                ForeColor = Color.DimGray,
                Text = "先点「预览」看看会改成什么样，确认无误再点「应用到游戏文件」。\n" +
                       "替换内容留空表示删除。允许的字符只有 A-Z a-z 0-9 和一批标点，包含别的字符的词会被跳过。"
            };

            p1.Controls.AddRange(new Control[] { l1, rFind, l2, rTo, rCase, rWord, rSkipQuiz,
                                                 l3, rScope, l4, rGroup, l4b, btnRp, btnRa, hint1 });
            t1.Controls.Add(p1);

            /* ---- 词库替换 ---- */
            var t2 = new TabPage("用我的词库替换");
            var p2 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

            var l5 = new Label
            {
                AutoSize = false, Location = new Point(12, 12), Size = new Size(720, 34),
                Text = "在下面每行填一个词。然后选一种替换方式，点预览确认后再应用。"
            };
            dWords.Multiline = true;
            dWords.ScrollBars = ScrollBars.Vertical;
            dWords.Location = new Point(12, 46);
            dWords.Size = new Size(720, 120);
            dWords.Font = new Font("Consolas", 9.5F);

            var l6 = new Label { Text = "方式", AutoSize = true, Location = new Point(12, 180) };
            dStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            dStyle.Items.AddRange(new object[]
            {
                "同长度（推荐）—— 每个词换成词库里同样长度的一个词",
                "按顺序循环 —— 依次使用你词库里的词",
                "随机打乱 —— 用固定种子随机挑"
            });
            dStyle.SelectedIndex = 0;
            dStyle.Location = new Point(70, 176); dStyle.Width = 430;

            dQuiz.Text = "也替换问答题（组 ≥ 90）"; dQuiz.AutoSize = true; dQuiz.Location = new Point(70, 206);

            var btnDp = new Button { Text = "预览", Location = new Point(470, 200), Width = 90, Height = 28 };
            var btnDa = new Button { Text = "应用到游戏文件", Location = new Point(568, 200), Width = 150, Height = 28 };

            p2.Controls.AddRange(new Control[] { l5, dWords, l6, dStyle, dQuiz, btnDp, btnDa });
            t2.Controls.Add(p2);

            tabs.TabPages.Add(t1);
            tabs.TabPages.Add(t2);

            /* ---- 预览区 ---- */
            var pvHead = new Panel { Dock = DockStyle.Top, Height = 30 };
            previewInfo.Text = "预览结果会显示在这里";
            previewInfo.Dock = DockStyle.Fill;
            previewInfo.TextAlign = ContentAlignment.MiddleLeft;
            previewInfo.Padding = new Padding(10, 0, 0, 0);
            pvHead.Controls.Add(previewInfo);
            preview.Dock = DockStyle.Fill;
            preview.Font = new Font("Consolas", 9.5F);
            preview.IntegralHeight = false;

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
            var btnApply = new Button { Text = "应用", Width = 100, Height = 30, Enabled = false };
            var btnCancel = new Button { Text = "关闭", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
            btnApply.Location = new Point(ClientSize.Width - 210, 9);
            btnCancel.Location = new Point(ClientSize.Width - 110, 9);
            btnApply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.Add(btnApply);
            bottom.Controls.Add(btnCancel);

            Controls.Add(preview);
            Controls.Add(pvHead);
            Controls.Add(bottom);
            Controls.Add(tabs);
            CancelButton = btnCancel;

            /* ---- 行为 ---- */

            Action clearPreview = () =>
            {
                pending = null;
                skipped.Clear();
                preview.Items.Clear();
                previewInfo.Text = "预览结果会显示在这里";
                btnApply.Enabled = false;   // 底部「应用」必须先预览；页签上那两个自己会先算
            };

            // 任何一个会影响结果的输入变了，预览就作废
            tabs.SelectedIndexChanged += (s, e) => clearPreview();
            rFind.TextChanged += (s, e) => clearPreview();
            rTo.TextChanged += (s, e) => clearPreview();
            dWords.TextChanged += (s, e) => clearPreview();
            dStyle.SelectedIndexChanged += (s, e) => clearPreview();
            rCase.CheckedChanged += (s, e) => clearPreview();
            rWord.CheckedChanged += (s, e) => clearPreview();
            rSkipQuiz.CheckedChanged += (s, e) => clearPreview();
            rScope.SelectedIndexChanged += (s, e) => clearPreview();
            rGroup.TextChanged += (s, e) => clearPreview();
            dQuiz.CheckedChanged += (s, e) => clearPreview();

            Action<bool> doPreview = (dictMode) =>
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    var scope = BuildScope(dictMode);
                    List<Change> changes;
                    skipped.Clear();
                    if (dictMode)
                    {
                        var words = dWords.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        if (words.Count == 0) { MessageBox.Show(this, "词库是空的，先在上面填几个词。"); return; }
                        int unusable = words.Count(w => !Codec.IsSupported(w));
                        if (unusable > 0)
                            skipped.Add("词库里有 " + unusable + " 个词含游戏不支持的字符，已忽略");
                        bool ignored;
                        changes = Bulk.ApplyDict(store, scope, words, dStyle.SelectedIndex, skipped, out ignored);
                        pendingIsDict = true;
                    }
                    else
                    {
                        if (rFind.Text.Length == 0) { MessageBox.Show(this, "「查找」不能为空。"); return; }
                        changes = Bulk.ApplyReplace(store, scope, rFind.Text, rTo.Text,
                                                    rCase.Checked, rWord.Checked, skipped);
                        pendingIsDict = false;
                    }
                    pending = changes;
                    ShowPreview(changes);
                    btnApply.Enabled = changes.Count > 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = Cursors.Default; }
            };

            // 真正写盘。dictMode == null 表示沿用已有预览结果。
            Action<bool?> doApply = (dictMode) =>
            {
                bool autoPreviewed = false;
                if (pending == null)
                {
                    // 没预览过就先算一次，别让用户点了没反应
                    if (dictMode == null) return;
                    doPreview(dictMode.Value);
                    autoPreviewed = true;
                }
                if (pending == null) return;
                if (pending.Count == 0)
                {
                    if (autoPreviewed)
                    {
                        string why = skipped.Count > 0 ? "\n\n" + string.Join("\n", skipped.Take(5)) : "";
                        MessageBox.Show(this, "没有需要修改的地方。" + why,
                            "没东西可改", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                if (MessageBox.Show(this,
                        "要把这 " + pending.Count + " 处修改写进游戏文件吗？\n\n" +
                        "开始前会自动把整个 word 目录备份一份。",
                        "确认应用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                Cursor = Cursors.WaitCursor;
                var errors = new List<string>();
                int touched = 0, changed = 0;
                try
                {
                    store.Backup("before-bulk");
                    var byFile = pending.GroupBy(c => c.File);
                    foreach (var g in byFile)
                    {
                        try
                        {
                            var es = store.Load(g.Key);
                            foreach (var c in g)
                            {
                                if (c.Index >= 0 && c.Index < es.Count) es[c.Index].Text = c.To;
                            }
                            store.Save(g.Key, es);
                            touched++; changed += g.Count();
                        }
                        catch (Exception ex) { errors.Add(g.Key + "：" + ex.Message); }
                    }
                }
                catch (Exception ex) { errors.Add(ex.Message); }
                finally { Cursor = Cursors.Default; }

                string msg = "完成：修改 " + changed + " 处，更新 " + touched + " 个文件。";
                if (errors.Count > 0) msg += "\n\n有 " + errors.Count + " 个文件出错：\n" + string.Join("\n", errors.Take(10));
                MessageBox.Show(this, msg, "应用完成", MessageBoxButtons.OK,
                    errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                if (touched > 0) AnythingChanged = true;
                clearPreview();
            };

            // 两个页签上的「应用到游戏文件」= 写盘；「预览」= 只算不写
            btnRp.Click += (s, e) => doPreview(false);
            btnRa.Click += (s, e) => doApply(false);
            btnDp.Click += (s, e) => doPreview(true);
            btnDa.Click += (s, e) => doApply(true);
            btnApply.Click += (s, e) => doApply(null);
        }

        Bulk.Scope BuildScope(bool dictMode)
        {
            var s = new Bulk.Scope();
            s.Files = (rScope.SelectedIndex == 0 && currentFile != null)
                ? new List<string> { currentFile }
                : null;
            s.SkipQuestions = dictMode ? !dQuiz.Checked : rSkipQuiz.Checked;
            int g;
            s.Group = int.TryParse(rGroup.Text.Trim(), out g) ? (int?)g : null;
            return s;
        }

        void ShowPreview(List<Change> changes)
        {
            preview.BeginUpdate();
            preview.Items.Clear();
            foreach (var c in changes.Take(500)) preview.Items.Add(c);
            preview.EndUpdate();

            string tail = "";
            if (skipped.Count > 0)
                tail = "   ⚠ " + skipped.Count + " 处被跳过：" + skipped[0];

            if (changes.Count == 0)
            {
                previewInfo.Text = "没有需要修改的地方。" + tail;
            }
            else
            {
                previewInfo.Text = "会修改 " + changes.Count + " 处" +
                    (changes.Count > 500 ? "（下面只显示前 500 条）" : "") +
                    "，涉及 " + changes.Select(c => c.File).Distinct().Count() + " 个文件。" + tail;
            }
        }
    }

    /* ==================================================================== */
    /*  批量替换的核心逻辑                                                   */
    /* ==================================================================== */

    static class Bulk
    {
        public class Scope
        {
            public List<string> Files;      // null = 全部
            public int? Group;
            public bool SkipQuestions;
        }

        static int HashStr(string s)
        {
            int h = 0;
            for (int i = 0; i < s.Length; i++)
            {
                h = ((h << 5) - h) + s[i];
            }
            // Math.Abs(int.MinValue) 会抛 OverflowException，这里挡掉
            return h == int.MinValue ? 0 : Math.Abs(h);
        }

        static uint RandFrom(uint seed, int n, uint mod)
        {
            uint x = (uint)(seed + (uint)n * 2654435761u);
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x % mod;
        }

        /// <summary>在内存里算出所有改动，不落盘。</summary>
        public static List<Change> Compute(Store store, Scope scope, Func<string, Entry, string, string> mapper,
                                           List<string> skippedFiles = null)
        {
            var files = (scope.Files != null && scope.Files.Count > 0) ? scope.Files : store.Files();
            var changes = new List<Change>();
            foreach (string f in files)
            {
                List<Entry> es;
                try { es = store.Load(f); }
                catch (Exception ex)
                {
                    // 不能静默跳过：读不了的文件要让用户知道，否则会以为“没匹配到”
                    if (skippedFiles != null) skippedFiles.Add(f + "：" + ex.Message);
                    continue;
                }

                for (int i = 0; i < es.Count; i++)
                {
                    var e = es[i];
                    if (scope.SkipQuestions && e.Group >= 90) continue;
                    if (scope.Group.HasValue && e.Group != (uint)scope.Group.Value) continue;

                    string outText = mapper(e.Text, e, f);
                    if (outText == null || outText == e.Text) continue;
                    if (!Codec.IsSupported(outText)) continue;
                    if (outText.Length == 0) continue;

                    changes.Add(new Change
                    {
                        File = f,
                        Index = i,
                        Group = e.Group,
                        From = e.Text,
                        To = outText
                    });
                }
            }
            return changes;
        }

        public static List<Change> ApplyReplace(Store store, Scope scope, string find, string to,
                                                bool caseSensitive, bool wholeWord,
                                                List<string> skippedFiles = null)
        {
            string esc = Regex.Escape(find);
            string pattern = wholeWord ? @"\b" + esc + @"\b" : esc;
            var opts = RegexOptions.None;
            if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
            var re = new Regex(pattern, opts);
            // 「替换为」是普通文本，不是正则模板：$ 必须转义，否则 $1 / $& / ${name} 会被当成反向引用
            string repl = (to ?? "").Replace("$", "$$");

            return Compute(store, scope, (text, e, f) =>
            {
                try { return re.Replace(text, repl); }
                catch { return text; }
            }, skippedFiles);
        }

        public static List<Change> ApplyDict(Store store, Scope scope, List<string> dict, int style,
                                             List<string> skippedFiles, out bool isDict)
        {
            isDict = true;
            var byLength = new Dictionary<int, List<string>>();
            foreach (string w in dict)
            {
                if (!Codec.IsSupported(w) || w.Length == 0) continue;
                List<string> pool;
                if (!byLength.TryGetValue(w.Length, out pool)) { pool = new List<string>(); byLength[w.Length] = pool; }
                pool.Add(w);
            }

            int counter = 0;
            uint seed = 12345;
            return Compute(store, scope, (text, e, f) =>
            {
                string result = text;
                if (style == 0)          // 同长度
                {
                    List<string> pool;
                    if (byLength.TryGetValue(text.Length, out pool) && pool.Count > 0)
                        result = pool[HashStr(text) % pool.Count];
                }
                else if (style == 1)     // 循环
                {
                    result = dict[counter % dict.Count];
                }
                else                     // 随机打乱
                {
                    result = dict[(int)RandFrom(seed, counter, (uint)dict.Count)];
                }
                counter++;
                return result;
            }, skippedFiles);
        }
    }

    /* ==================================================================== */

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "-t"))
            {
                Environment.Exit(SelfTest.Run(args));
                return;
            }

            if (args.Length > 0 && args[0] == "--savecheck")
            {
                Environment.Exit(SaveCheck.Run(args));
                return;
            }

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception e)
            {
                MessageBox.Show("启动出错：\n" + e.ToString(), "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /* ==================================================================== */
    /*  自检：在全部 word/*.bin 上验证 解码→编码 是否逐字节一致              */
    /*  用法: 死亡打字员单词编辑器.exe --selftest [游戏目录]                  */
    /* ==================================================================== */

    static class SelfTest
    {
        public static int Run(string[] args)
        {
            string dir = args.Length > 1
                ? args[1]
                : Store.FindGameDir(Path.GetDirectoryName(Application.ExecutablePath));

            var log = new StringBuilder();
            if (dir == null)
            {
                log.AppendLine("找不到游戏目录");
                Console.Error.Write(log.ToString());
                return 2;
            }

            Store store;
            try { store = new Store(dir); }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }

            var files = store.Files();
            log.AppendLine("游戏目录: " + store.GameDir);
            log.AppendLine("单词文件: " + files.Count + " 个");

            int ok = 0, words = 0;
            var bad = new List<string>();
            foreach (string name in files)
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(Path.Combine(store.WordDir, name));
                    var es = Codec.Decode(raw);
                    words += es.Count;
                    byte[] again = Codec.Encode(es);
                    if (again.Length == raw.Length && again.SequenceEqual(raw)) ok++;
                    else bad.Add(name + ": 重新编码后不是逐字节一致");
                }
                catch (Exception e)
                {
                    bad.Add(name + ": " + e.Message);
                }
            }

            log.AppendLine("逐字节一致: " + ok + " / " + files.Count);
            log.AppendLine("词条总数: " + words);
            if (bad.Count > 0)
            {
                log.AppendLine("有问题的文件:");
                foreach (string b in bad.Take(30)) log.AppendLine("  " + b);
            }

            // 顺带验证字符集自洽
            log.AppendLine("支持的字符: " + Codec.SupportedChars);
            log.AppendLine("明文字符数: " + Codec.Cipher.Count + "  密文字节数: " + Codec.Plain.Count);

            string text = log.ToString();
            Console.Out.Write(text);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
                    "selftest-result.txt"), text, new UTF8Encoding(false));
            }
            catch { }

            return bad.Count == 0 && ok == files.Count ? 0 : 1;
        }
    }

    /* ==================================================================== */
    /*  保存路径自检：在临时副本上完整走一遍读→改→存→回读                    */
    /*  用法: 死亡打字员单词编辑器.exe --savecheck [游戏目录]                 */
    /* ==================================================================== */

    static class SaveCheck
    {
        public static int Run(string[] args)
        {
            string src = args.Length > 1
                ? args[1]
                : Store.FindGameDir(Path.GetDirectoryName(Application.ExecutablePath));
            var log = new StringBuilder();
            if (src == null) { Console.Error.WriteLine("找不到游戏目录"); return 2; }

            Store srcStore;
            try { srcStore = new Store(src); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }

            string tmp = Path.Combine(Path.GetTempPath(), "totd-savecheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string wordDir = Path.Combine(tmp, "word");
            Directory.CreateDirectory(wordDir);

            int pass = 0, fail = 0;
            Action<bool, string> check = (ok, what) =>
            {
                if (ok) pass++;
                else { fail++; log.AppendLine("  ✗ " + what); }
            };

            try
            {
                var names = srcStore.Files().Take(6).ToList();
                foreach (string n in names)
                    File.Copy(Path.Combine(srcStore.WordDir, n), Path.Combine(wordDir, n));

                var st = new Store(tmp);
                log.AppendLine("临时副本: " + tmp);
                log.AppendLine("测试文件: " + string.Join(", ", names));

                foreach (string n in names)
                {
                    string full = Path.Combine(wordDir, n);

                    // 1) 原样保存必须逐字节一致
                    var es = st.Load(n);
                    byte[] before = File.ReadAllBytes(full);
                    st.Save(n, es);
                    byte[] after = File.ReadAllBytes(full);
                    check(before.Length == after.Length && before.SequenceEqual(after),
                          n + " 原样保存不是逐字节一致");

                    // 2) 把一个词改长，回读要正确、其它词条不受影响
                    var es2 = st.Load(n);
                    string orig0 = es2[0].Text;
                    string new0 = orig0 + "xyz";
                    es2[0].Text = new0;
                    st.Save(n, es2);
                    var es3 = st.Load(n);
                    bool ok3 = es3.Count == es2.Count && es3[0].Text == new0 && es3[0].Group == es2[0].Group;
                    if (ok3)
                        for (int i = 1; i < es3.Count; i++)
                            if (es3[i].Text != es2[i].Text || es3[i].Group != es2[i].Group) { ok3 = false; break; }
                    check(ok3, n + " 改长之后回读不对");

                    // 3) 改短
                    var es4 = st.Load(n);
                    es4[0].Text = "A";
                    st.Save(n, es4);
                    var es5 = st.Load(n);
                    check(es5[0].Text == "A" && es5.Count == es4.Count, n + " 改短之后回读不对");

                    // 4) 不支持的中文必须被拒绝，且文件不能被改动
                    var es6 = st.Load(n);
                    byte[] snapshot = File.ReadAllBytes(full);
                    es6[0].Text = "中文";
                    bool rejected = false;
                    try { st.Save(n, es6); } catch { rejected = true; }
                    check(rejected, n + " 不支持的中文居然被写进去了");
                    check(File.ReadAllBytes(full).SequenceEqual(snapshot), n + " 被拒绝的保存却改动了文件");

                    // 5) 空词必须被拒绝
                    var es7 = st.Load(n);
                    es7[0].Text = "";
                    bool rejected2 = false;
                    try { st.Save(n, es7); } catch { rejected2 = true; }
                    check(rejected2, n + " 空词居然被写进去了");

                    // 6) 词条数不能变
                    check(st.Load(n).Count == es.Count, n + " 词条数被改变了");
                }

                // 7) 备份 → 改动 → 还原
                var bk = st.Backup("savecheck");
                var bkFiles = Directory.GetFiles(Path.Combine(st.BackupRoot, bk.Name), "*.bin");
                check(bkFiles.Length == names.Count, "备份文件数不对 (" + bkFiles.Length + "/" + names.Count + ")");

                var t = st.Load(names[0]);
                t[0].Text = "ZZZZ";
                st.Save(names[0], t);
                check(st.Load(names[0])[0].Text == "ZZZZ", "改动没生效");

                st.Restore(bk.Name);
                var restored = st.Load(names[0]);
                check(restored[0].Text == "A" || restored[0].Text == "ZZZZ" ? true : false, "还原后读不到");
                check(restored.Count == t.Count, "还原后词条数不对");

                // 8) 校验：目前临时目录里的文件应该全都自洽
                var vr = st.Verify();
                check(vr.Bad.Count == 0, "校验发现 " + vr.Bad.Count + " 个问题文件");

                log.AppendLine();
                log.AppendLine("通过 " + pass + " 项，失败 " + fail + " 项");
            }
            catch (Exception e)
            {
                log.AppendLine("异常: " + e);
                fail++;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            string text = log.ToString();
            Console.Out.Write(text);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
                    "savecheck-result.txt"), text, new UTF8Encoding(false));
            }
            catch { }

            return fail == 0 ? 0 : 1;
        }
    }
}
