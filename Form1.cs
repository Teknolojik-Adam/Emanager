using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;

namespace teny_desk
{
    public partial class Form1 : Form
    {
        private string currentPath = "C:\\";
        private List<ListViewItem> allItemsCache = new List<ListViewItem>();
        private bool isDarkMode = false;
        private const string searchPlaceholder = "Ara...";
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        
        // Clipboard işlemleri
        private List<string> clipboardItems = new List<string>();
        private string clipboardMode = ""; // "copy", "cut", ""
        private Stack<string> navigationHistory = new Stack<string>();
        private Stack<string> navigationFuture = new Stack<string>();

        public Form1()
        {
            InitializeComponent();

            
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                           ?.SetValue(listView1, true, null);

            InitializeIconList();
            this.KeyPreview = true;
            this.KeyDown += new KeyEventHandler(Form1_KeyDown);
            this.FormClosing += new FormClosingEventHandler(Form1_FormClosing);
            
            // Sağ tık menüsü öğeleri ekle
            AddContextMenuItems();
        }

        private void InitializeIconList()
        {
            try
            {
                iconlist.ImageSize = new Size(24, 24);
                iconlist.ColorDepth = ColorDepth.Depth32Bit;
                
                // Varsayılan ikonlar
                iconlist.Images.Add("folder", SystemIcons.WinLogo.ToBitmap());
                iconlist.Images.Add("file", SystemIcons.Application.ToBitmap());
                iconlist.Images.Add("picture", SystemIcons.Question.ToBitmap());
                
                // Ortak dosya türlerinin ikonları (extension-based)
                AddIconMapping("txt", "📄 Text File");
                AddIconMapping("doc", "📄 Document");
                AddIconMapping("docx", "📄 Document");
                AddIconMapping("pdf", "📕 PDF");
                AddIconMapping("xls", "📊 Excel");
                AddIconMapping("xlsx", "📊 Excel");
                AddIconMapping("ppt", "📽️ PowerPoint");
                AddIconMapping("pptx", "📽️ PowerPoint");
                AddIconMapping("zip", "📦 Archive");
                AddIconMapping("rar", "📦 Archive");
                AddIconMapping("7z", "📦 Archive");
                AddIconMapping("exe", "⚙️ Executable");
                AddIconMapping("dll", "⚙️ Library");
                AddIconMapping("jpg", "🖼️ Image");
                AddIconMapping("jpeg", "🖼️ Image");
                AddIconMapping("png", "🖼️ Image");
                AddIconMapping("gif", "🖼️ Image");
                AddIconMapping("bmp", "🖼️ Image");
                AddIconMapping("mp3", "🎵 Audio");
                AddIconMapping("wav", "🎵 Audio");
                AddIconMapping("mp4", "🎬 Video");
                AddIconMapping("avi", "🎬 Video");
                AddIconMapping("mkv", "🎬 Video");
                AddIconMapping("html", "🌐 HTML");
                AddIconMapping("css", "🌐 CSS");
                AddIconMapping("js", "🌐 JavaScript");
                AddIconMapping("cs", "🔧 C#");
                AddIconMapping("java", "☕ Java");
                AddIconMapping("py", "🐍 Python");
                AddIconMapping("cpp", "🔧 C++");
                AddIconMapping("h", "🔧 Header");
                
                
                string iconDirectory = Path.Combine(Application.StartupPath, "Resources");
                if (Directory.Exists(iconDirectory))
                {
                    var imageFiles = Directory.GetFiles(iconDirectory, "*.png");
                    foreach (var file in imageFiles)
                    {
                        try
                        {
                            string iconName = Path.GetFileNameWithoutExtension(file);
                            if (!iconlist.Images.ContainsKey(iconName))
                            {
                                iconlist.Images.Add(iconName, Image.FromFile(file));
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Warn($"İkon yüklenirken hata ({file}): {ex.Message}");
                        }
                    }
                }
                
                logger.Info($"İkon sistemi başlatıldı - {iconlist.Images.Count} ikon yüklendi");
            }
            catch (Exception ex)
            {
                logger.Error("İkons sistemi başlatılırken hata: " + ex.Message);
            }
        }
        
        private void AddIconMapping(string extension, string description)
        {
            if (!iconlist.Images.ContainsKey(extension))
            {
                // Varsayılan olarak file ikonunu kullan
                try
                {
                    // Sistem ikonlarından almaya çalış
                    var icon = SystemIcons.Application;
                    iconlist.Images.Add(extension, icon.ToBitmap());
                }
                catch
                {
                    // hata olursa picture iconunuonu al
                    if (!iconlist.Images.ContainsKey("picture"))
                        iconlist.Images.Add(extension, SystemIcons.Question.ToBitmap());
                }
            }
        }

        private void AddContextMenuItems()
        {
            try
            {
                // Yeni menü öğeleri ekle
                var copyMenuItem = new ToolStripMenuItem("Kopyala (Ctrl+C)") { Name = "copyMenuItem" };
                copyMenuItem.Click += (s, e) => CopyFiles();
                
                var cutMenuItem = new ToolStripMenuItem("Taşı (Ctrl+X)") { Name = "cutMenuItem" };
                cutMenuItem.Click += (s, e) => CutFiles();
                
                var pasteMenuItem = new ToolStripMenuItem("Yapıştır (Ctrl+V)") { Name = "pasteMenuItem" };
                pasteMenuItem.Click += (s, e) => PasteFiles();
                
                var renameMenuItem = new ToolStripMenuItem("Yeniden Adlandır (F2)") { Name = "renameMenuItem" };
                renameMenuItem.Click += (s, e) => RenameFile();
                
                var terminalMenuItem = new ToolStripMenuItem("Terminali Burada Aç") { Name = "terminalMenuItem" };
                terminalMenuItem.Click += (s, e) => OpenTerminalHere();
                
                // Var olan separator'dan sonra ekle
                int separatorIndex = contextMenuStrip1.Items.IndexOf(toolStripSeparator2);
                contextMenuStrip1.Items.Insert(separatorIndex + 1, copyMenuItem);
                contextMenuStrip1.Items.Insert(separatorIndex + 2, cutMenuItem);
                contextMenuStrip1.Items.Insert(separatorIndex + 3, pasteMenuItem);
                contextMenuStrip1.Items.Insert(separatorIndex + 4, renameMenuItem);
                contextMenuStrip1.Items.Insert(separatorIndex + 5, new ToolStripSeparator());
                contextMenuStrip1.Items.Insert(separatorIndex + 6, terminalMenuItem);
            }
            catch (Exception ex)
            {
                logger.Error("Context menu öğeleri eklenirken hata: " + ex.Message);
            }
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                // Ayarları yükle
                AppSettings.Load();
                isDarkMode = AppSettings.DarkModeEnabled;
                currentPath = AppSettings.LastDirectory;
                
                // Dizin varsa git, yoksa varsayılan
                if (!Directory.Exists(currentPath))
                    currentPath = "C:\\";
                
                filetexbox.Text = currentPath;
                txtSearch.Text = searchPlaceholder;
                txtSearch.ForeColor = Color.Gray;
                LoadDrives();
                ApplyTheme();
                
                logger.Info($"Uygulama başlatıldı - Son dizin: {currentPath}");
                await LoadFilesAndFoldersAsync();
            }
            catch (Exception ex)
            {
                logger.Error("Form yüklemede hata: " + ex.Message);
                MessageBox.Show("Uygulama yüklenirken hata oluştu: " + ex.Message);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // Ayarları kaydet
                AppSettings.LastDirectory = currentPath;
                AppSettings.DarkModeEnabled = isDarkMode;
                AppSettings.Save();
                
                logger.Info("Uygulama kapatıldı");
            }
            catch (Exception ex)
            {
                logger.Error("Ayarlar kaydedilirken hata: " + ex.Message);
            }
        }

        #region Theme Management

        private void ApplyTheme()
        {
            try
            {
                ITheme theme = isDarkMode ? (ITheme)DarkTheme.Instance : (ITheme)LightTheme.Instance;

                this.BackColor = theme.BackColor;
                this.ForeColor = theme.ForeColor;

                toolStrip1.BackColor = theme.BackColor;
                toolStrip1.ForeColor = theme.ForeColor;
                statusStrip1.BackColor = theme.BackColor;
                statusStrip1.ForeColor = theme.ForeColor;
                panelNav.BackColor = theme.BackColor;

                listView1.BackColor = theme.BackColor;
                listView1.ForeColor = theme.ForeColor;

                ApplyControlTheme(filetexbox, theme);
                ApplyControlTheme(txtSearch, theme);
                ApplyControlTheme(comboBoxDrives, theme);

                contextMenuStrip1.BackColor = theme.BackColor;
                contextMenuStrip1.ForeColor = theme.ForeColor;

                foreach (ToolStripItem item in toolStrip1.Items)
                {
                    if (item is ToolStripButton btn)
                    {
                        btn.ForeColor = theme.ForeColor;
                    }
                    else if (item is ToolStripLabel lbl)
                    {
                        lbl.ForeColor = theme.ForeColor;
                    }
                }

                foreach (ToolStripItem item in contextMenuStrip1.Items)
                {
                    item.ForeColor = theme.ForeColor;
                }
                
                logger.Debug($"Tema uygulandı: {(isDarkMode ? "Koyu" : "Aydınlık")}");
            }
            catch (Exception ex)
            {
                logger.Error("Tema uygulanırken hata: " + ex.Message);
            }
        }

        private void ApplyControlTheme(Control ctrl, ITheme theme)
        {
            ctrl.BackColor = theme.TextBoxBackColor;
            ctrl.ForeColor = theme.ForeColor;
        }

        private void btnToggleTheme_Click(object sender, EventArgs e)
        {
            isDarkMode = !isDarkMode;
            btnToggleTheme.Text = isDarkMode ? "☀ Açık Mod" : "🌙 Karanlık Mod";
            ApplyTheme();
            logger.Info($"Tema değiştirildi: {(isDarkMode ? "Koyu" : "Aydınlık")}");
        }

        #endregion

        private void LoadDrives()
        {
            comboBoxDrives.Items.Clear();
            try
            {
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                foreach (DriveInfo d in allDrives)
                {
                    if (d.IsReady)
                    {
                        string displayName = $"{d.Name} ({d.VolumeLabel ?? "Bilinmiyor"})";
                        comboBoxDrives.Items.Add(displayName);
                    }
                }
                if (comboBoxDrives.Items.Count > 0)
                    comboBoxDrives.SelectedIndex = 0;
                    
                logger.Debug($"{comboBoxDrives.Items.Count} sürücü yüklendi");
            }
            catch (Exception ex)
            {
                logger.Error("Sürücüler yüklenirken hata: " + ex.Message);
                MessageBox.Show("Sürücüler yüklenirken hata: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadFilesAndFoldersAsync()
        {
            try
            {
                if (!Directory.Exists(currentPath))
                {
                    logger.Warn($"Dizin bulunamadı: {currentPath}");
                    MessageBox.Show("Dizin bulunamadı: " + currentPath, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    await GoBackAsync();
                    return;
                }
                toolStripStatusLabel1.Text = "Yükleniyor...";
                this.Cursor = Cursors.WaitCursor;
                allItemsCache.Clear();

                List<ListViewItem> items = await Task.Run(() =>
                {
                    var itemList = new List<ListViewItem>();
                    var dirInfo = new DirectoryInfo(currentPath);
                    if (dirInfo.Parent != null)
                    {
                        var parentItem = new ListViewItem("..", "folder");
                        parentItem.SubItems.AddRange(new[] { "", "Klasör", "" });
                        itemList.Add(parentItem);
                    }
                    try
                    {
                        foreach (var dir in dirInfo.GetDirectories())
                        {
                            var item = new ListViewItem(dir.Name, "folder");
                            item.SubItems.AddRange(new[] { "", "Klasör", dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                            itemList.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.Warn($"Klasörlere erişim reddedildi: {ex.Message}");
                    }
                    try
                    {
                        foreach (var file in dirInfo.GetFiles())
                        {
                            string ext = file.Extension.ToLower().Replace(".", "");
                            string iconKey = iconlist.Images.ContainsKey(ext) ? ext : "file";
                            var item = new ListViewItem(file.Name, iconKey);
                            item.SubItems.AddRange(new[] { FormatFileSize(file.Length), GetFileTypeName(file.Extension), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                            itemList.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.Warn($"Dosyalara erişim reddedildi: {ex.Message}");
                    }
                    return itemList;
                });

                listView1.BeginUpdate();
                listView1.Items.Clear();
                allItemsCache.AddRange(items);
                listView1.Items.AddRange(items.ToArray());
                listView1.EndUpdate();
                UpdateStatusBar();
                
                logger.Info($"Dizin yüklendi: {currentPath} ({items.Count} öğe)");
            }
            catch (Exception ex)
            {
                logger.Error($"Dizin yüklenirken hata: {ex.Message}");
                MessageBox.Show($"Dizin yüklenirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        

        #region Performance Optimization
        
        /// <summary>
        /// Dosya boyutunu insan tarafından okunabilir formata çevirir
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size = size / 1024;
            }
            
            return $"{Math.Round(size, 1)} {sizes[order]}";
        }
        
        /// <summary>
        /// Dosya türü adını oluşturur
        /// </summary>
        private string GetFileTypeName(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return "Dosya";
            
            extension = extension.ToLower().TrimStart('.');
            
            // Bilinen türler
            var typeMappings = new Dictionary<string, string>
            {
                {"txt", "Metin Dosyası"},
                {"doc", "Word Belgesi"}, {"docx", "Word Belgesi"},
                {"pdf", "PDF Belgesi"},
                {"xls", "Excel Tablosu"}, {"xlsx", "Excel Tablosu"},
                {"ppt", "PowerPoint Sunusu"}, {"pptx", "PowerPoint Sunusu"},
                {"zip", "Sıkıştırılmış Dosya"}, {"rar", "Sıkıştırılmış Dosya"}, {"7z", "Sıkıştırılmış Dosya"},
                {"exe", "Çalıştırılabilir Dosya"}, {"msi", "Kurulum Dosyası"},
                {"dll", "Sistem Dosyası"}, {"sys", "Sistem Dosyası"},
                {"jpg", "JPEG Resmi"}, {"jpeg", "JPEG Resmi"}, {"png", "PNG Resmi"}, {"gif", "GIF Resmi"}, {"bmp", "Bitmap Resmi"},
                {"mp3", "Ses Dosyası"}, {"wav", "Ses Dosyası"}, {"flac", "Ses Dosyası"},
                {"mp4", "Video Dosyası"}, {"avi", "Video Dosyası"}, {"mkv", "Video Dosyası"}, {"mov", "Video Dosyası"},
                {"html", "HTML Dosyası"}, {"htm", "HTML Dosyası"},
                {"css", "CSS Dosyası"},
                {"js", "JavaScript Dosyası"},
                {"json", "JSON Dosyası"},
                {"xml", "XML Dosyası"},
                {"cs", "C# Dosyası"},
                {"java", "Java Dosyası"},
                {"py", "Python Dosyası"},
                {"cpp", "C++ Dosyası"}, {"c", "C Dosyası"}, {"h", "Header Dosyası"}
            };
            
            if (typeMappings.ContainsKey(extension))
                return typeMappings[extension];
            
            return $"{extension.ToUpper()} Dosyası";
        }
        
        #endregion
        private void UpdateStatusBar()
        {
            int folderCount = allItemsCache.Count(item => item.SubItems[2].Text == "Klasör" && item.Text != "..");
            int fileCount = allItemsCache.Count - folderCount - (allItemsCache.Any(i => i.Text == "..") ? 1 : 0);
            
            long totalSize = 0;
            foreach (ListViewItem item in allItemsCache)
            {
                if (item.Text != ".." && item.SubItems[2].Text != "Klasör")
                {
                    if (long.TryParse(item.SubItems[1].Text.Split()[0], out long size))
                    {
                      
                    }
                }
            }
            
            toolStripStatusLabel1.Text = $"{folderCount} klasör, {fileCount} dosya";
            toolStripStatusLabel2.Text = $"{currentPath}";
        }

        private async void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;
                if (selectedName == "..")
                {
                    await GoBackAsync();
                    return;
                }

                string fullPath = Path.Combine(currentPath, selectedName);
                if (Directory.Exists(fullPath))
                {
                    navigationHistory.Push(currentPath);
                    navigationFuture.Clear();
                    
                    currentPath = fullPath;
                    filetexbox.Text = currentPath;
                    
                    logger.Debug($"Klasöre gidildi: {currentPath}");
                    await LoadFilesAndFoldersAsync();
                }
                else if (File.Exists(fullPath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                        logger.Info($"Dosya açıldı: {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Dosya açılırken hata ({fullPath}): {ex.Message}");
                        MessageBox.Show($"Dosya açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private async Task GoBackAsync()
        {
            try
            {
                if (currentPath.Length <= 3)
                {
                    logger.Debug("Zaten root dizindeyiz");
                    return;
                }

                // History'ye ekle
                navigationFuture.Push(currentPath);
                
                string parentPath = Directory.GetParent(currentPath)?.FullName;
                if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                {
                    currentPath = parentPath;
                    filetexbox.Text = currentPath;
                    
                    logger.Debug($"Geri gidildi: {currentPath}");
                    await LoadFilesAndFoldersAsync();
                }
            }
            catch (Exception ex)
            {
                logger.Error("Geri gidilirken hata: " + ex.Message);
                MessageBox.Show($"Geri gidilirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void GoToPath()
        {
            if (Directory.Exists(filetexbox.Text))
            {
                currentPath = filetexbox.Text;
                navigationHistory.Push(currentPath);
                navigationFuture.Clear();
                LoadFilesAndFoldersAsync();
            }
            else { MessageBox.Show("Dizin mevcut değil!", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        #region Dosya İşlemleri
        
        private void CopyFiles()
        {
            if (listView1.SelectedItems.Count == 0 || listView1.SelectedItems[0].Text == "..") return;
            
            try
            {
                clipboardItems.Clear();
                clipboardMode = "copy";
                
                int count = 0;
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    if (item.Text != "..")
                    {
                        string fullPath = Path.Combine(currentPath, item.Text);
                        clipboardItems.Add(fullPath);
                        count++;
                    }
                }
                
                logger.Info($"Kopyalandı: {count} öğe - {string.Join(", ", clipboardItems.Take(3).ToList())}");
                
                if (count <= 5)
                    MessageBox.Show($"{count} öğe kopyalandı.", "Kopyala", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                logger.Error("Kopyalama hatası: " + ex.Message);
                MessageBox.Show($"Kopyalama hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CutFiles()
        {
            if (listView1.SelectedItems.Count == 0 || listView1.SelectedItems[0].Text == "..") return;
            
            try
            {
                clipboardItems.Clear();
                clipboardMode = "cut";
                
                int count = 0;
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    if (item.Text != "..")
                    {
                        string fullPath = Path.Combine(currentPath, item.Text);
                        clipboardItems.Add(fullPath);
                        count++;
                    }
                }
                
                logger.Info($"Taşımaya hazırlandı: {count} öğe");
                
                if (count <= 5)
                    MessageBox.Show($"{count} öğe taşınmaya hazır.", "Taşı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                logger.Error("Taşıma hazırlığında hata: " + ex.Message);
                MessageBox.Show($"Taşıma hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void PasteFiles()
        {
            if (clipboardItems.Count == 0 || string.IsNullOrEmpty(clipboardMode))
            {
                MessageBox.Show("Clipboard'da hiçbir işlem yok.", "Yapıştır", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                this.Cursor = Cursors.WaitCursor;
                int successCount = 0;
                int failCount = 0;
                
                foreach (string sourcePath in clipboardItems)
                {
                    try
                    {
                        if (File.Exists(sourcePath))
                        {
                            string destPath = Path.Combine(currentPath, Path.GetFileName(sourcePath));
                            
                            // Aynı isimde dosya varsa yeni ad ver
                            int counter = 1;
                            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
                            string extension = Path.GetExtension(sourcePath);
                            while (File.Exists(destPath))
                            {
                                destPath = Path.Combine(currentPath, $"{fileName} ({counter}){extension}");
                                counter++;
                            }
                            
                            if (clipboardMode == "copy")
                            {
                                File.Copy(sourcePath, destPath, false);
                                successCount++;
                            }
                            else if (clipboardMode == "cut")
                            {
                                File.Move(sourcePath, destPath);
                                successCount++;
                            }
                        }
                        else if (Directory.Exists(sourcePath))
                        {
                            string destPath = Path.Combine(currentPath, Path.GetFileName(sourcePath));
                            
                            // Aynı isimde klasör varsa yeni ad ver
                            int counter = 1;
                            string dirName = Path.GetFileName(sourcePath);
                            while (Directory.Exists(destPath))
                            {
                                destPath = Path.Combine(currentPath, $"{dirName} ({counter})");
                                counter++;
                            }
                            
                            if (clipboardMode == "copy")
                            {
                                CopyDirectory(sourcePath, destPath);
                                successCount++;
                            }
                            else if (clipboardMode == "cut")
                            {
                                Directory.Move(sourcePath, destPath);
                                successCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        logger.Warn($"Öğe işlenirken hata ({sourcePath}): {ex.Message}");
                    }
                }
                
                // Cut işleminde clipboard temizle
                if (clipboardMode == "cut")
                {
                    clipboardItems.Clear();
                    clipboardMode = "";
                }
                
                logger.Info($"Yapıştırma tamamlandı - Başarılı: {successCount}, Başarısız: {failCount}");
                
                if (successCount + failCount <= 5)
                    MessageBox.Show($"Başarılı: {successCount}, Başarısız: {failCount}", "Yapıştır Sonucu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                
                await LoadFilesAndFoldersAsync();
            }
            catch (Exception ex)
            {
                logger.Error("Yapıştırma hatası: " + ex.Message);
                MessageBox.Show($"Yapıştırma hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, false);
            }
            
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private void RenameFile()
        {
            if (listView1.SelectedItems.Count == 0 || listView1.SelectedItems[0].Text == "..") return;

            try
            {
                string oldName = listView1.SelectedItems[0].Text;
                string oldPath = Path.Combine(currentPath, oldName);
                
                // Input dialog göster
                InputDialog dialog = new InputDialog();
                dialog.DefaultValue = oldName;
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string newName = dialog.InputValue;
                    
                    if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
                        return;
                    
                    // Geçersiz karakterler kontrol et
                    if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        MessageBox.Show("Dosya adında geçersiz karakterler var!", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    
                    string newPath = Path.Combine(currentPath, newName);
                    
                    if (File.Exists(oldPath))
                        File.Move(oldPath, newPath);
                    else if (Directory.Exists(oldPath))
                        Directory.Move(oldPath, newPath);
                    
                    logger.Info($"Yeniden adlandırıldı: {oldName} → {newName}");
                    LoadFilesAndFoldersAsync();
                }
            }
            catch (Exception ex)
            {
                logger.Error("Yeniden adlandırma hatası: " + ex.Message);
                MessageBox.Show($"Yeniden adlandırma hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenTerminalHere()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = currentPath,
                    UseShellExecute = true
                };
                
                Process.Start(psi);
                logger.Info($"Terminal açıldı: {currentPath}");
            }
            catch (Exception ex)
            {
                logger.Error("Terminal açılırken hata: " + ex.Message);
                MessageBox.Show($"Terminal açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Event Handlers
        private void txtSearch_Enter(object sender, EventArgs e)
        {
            if (txtSearch.Text == searchPlaceholder) { txtSearch.Text = ""; txtSearch.ForeColor = isDarkMode ? Color.White : Color.Black; }
        }
        private void txtSearch_Leave(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSearch.Text)) { txtSearch.Text = searchPlaceholder; txtSearch.ForeColor = Color.Gray; }
        }
        private void filetexbox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                GoToPath();
                e.SuppressKeyPress = true;
            }
        }
        private async void geributton_Click(object sender, EventArgs e)
        {
            await GoBackAsync();
        }
        private async void toolStripButtonRefresh_Click(object sender, EventArgs e)
        {
            await LoadFilesAndFoldersAsync();
        }
        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            listView1_DoubleClick(sender, e);
        }
        private async void toolStripButtonDelete_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0 || listView1.SelectedItems[0].Text == "..") return;
            string fullPath = Path.Combine(currentPath, listView1.SelectedItems[0].Text);
            if (MessageBox.Show($"Bu öğe silinsin mi?\n{fullPath}", "Silme Onayı", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try
                {
                    if (Directory.Exists(fullPath))
                    {
                        Directory.Delete(fullPath, true);
                        logger.Info($"Klasör silindi: {fullPath}");
                    }
                    else
                    {
                        File.Delete(fullPath);
                        logger.Info($"Dosya silindi: {fullPath}");
                    }
                    await LoadFilesAndFoldersAsync();
                }
                catch (Exception ex)
                {
                    logger.Error($"Silme hatası ({fullPath}): {ex.Message}");
                    MessageBox.Show($"Silinirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        private void copyPathToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string fullPath = Path.Combine(currentPath, listView1.SelectedItems[0].Text);
                Clipboard.SetText(fullPath);
                logger.Info($"Yol kopyalandı: {fullPath}");
                MessageBox.Show("Yol kopyalandı!", "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                try
                {
                    string fullPath = Path.Combine(currentPath, listView1.SelectedItems[0].Text);
                    FileInfo fi = new FileInfo(fullPath);
                    DirectoryInfo di = new DirectoryInfo(fullPath);

                    string info = $"Yol: {fullPath}\n";
                    info += $"Ad: {Path.GetFileName(fullPath)}\n";
                    
                    if (Directory.Exists(fullPath))
                    {
                        info += $"Tür: Klasör\n";
                        info += $"Oluşturma Tarihi: {di.CreationTime:G}\n";
                        info += $"Değiştirilme Tarihi: {di.LastWriteTime:G}\n";
                    }
                    else if (File.Exists(fullPath))
                    {
                        info += $"Tür: Dosya\n";
                        info += $"Boyut: {FormatFileSize(fi.Length)}\n";
                        info += $"Oluşturma Tarihi: {fi.CreationTime:G}\n";
                        info += $"Değiştirilme Tarihi: {fi.LastWriteTime:G}\n";
                    }

                    MessageBox.Show(info, "Özellikler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    logger.Error("Özellikler alınırken hata: " + ex.Message);
                    MessageBox.Show($"Özellikler alınamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        private async void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedItem != null)
            {
                try
                {
                    string selectedDrive = comboBoxDrives.SelectedItem.ToString();
                    // İlk 2 karakteri al (C:, D: vs)
                    string drivePath = selectedDrive.Substring(0, 2) + "\\";
                    
                    currentPath = drivePath;
                    filetexbox.Text = currentPath;
                    navigationHistory.Clear();
                    navigationFuture.Clear();
                    
                    logger.Info($"Sürücü değiştirildi: {drivePath}");
                    await LoadFilesAndFoldersAsync();
                }
                catch (Exception ex)
                {
                    logger.Error("Sürücü seçiminde hata: " + ex.Message);
                    MessageBox.Show("Sürücü seçiminde hata: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        private async void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                await LoadFilesAndFoldersAsync();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Back)
            {
                await GoBackAsync();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Delete)
            {
                toolStripButtonDelete_Click(sender, e);
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Enter && listView1.Focused)
            {
                listView1_DoubleClick(sender, e);
                e.Handled = true;
            }
            
            // Keyboard shortcuts
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopyFiles();
                e.Handled = true;
            }
            if (e.Control && e.KeyCode == Keys.X)
            {
                CutFiles();
                e.Handled = true;
            }
            if (e.Control && e.KeyCode == Keys.V)
            {
                PasteFiles();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.F2)
            {
                RenameFile();
                e.Handled = true;
            }
        }
        private void txtSearch_TextChanged(object sender, EventArgs e)
        {
            string searchText = (txtSearch.Text == searchPlaceholder) ? "" : txtSearch.Text.ToLower().Trim();
            
            if (string.IsNullOrWhiteSpace(searchText))
            {
                listView1.BeginUpdate();
                listView1.Items.Clear();
                listView1.Items.AddRange(allItemsCache.ToArray());
                listView1.EndUpdate();
                return;
            }

            listView1.BeginUpdate();
            listView1.Items.Clear();
            
            // Wildcard pattern'i regex'e çevir (* ve ?)
            string pattern = "^" + Regex.Escape(searchText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
            
            var filteredItems = new List<ListViewItem>();
            
            foreach (var item in allItemsCache)
            {
                // ".." öğesini her zaman göster
                if (item.Text == "..")
                {
                    filteredItems.Add(item);
                }
                // Dosya/klasör adı eşleşme kontrolü
                else if (regex.IsMatch(item.Text))
                {
                    filteredItems.Add(item);
                }
                // Partial match (contains)
                else if (item.Text.ToLower().Contains(searchText))
                {
                    filteredItems.Add(item);
                }
            }
            
            listView1.Items.AddRange(filteredItems.ToArray());
            listView1.EndUpdate();
            
            logger.Debug($"Arama: '{searchText}' - {filteredItems.Count} sonuç");
        }
        private void listView1_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e) {  }

        private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            listView1.ListViewItemSorter = new ListViewItemComparer(e.Column,
                listView1.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending);
            listView1.Sort();
        }
        #endregion
    }

    #region Theme Interfaces and Classes
    public interface ITheme
    {
        Color BackColor { get; }
        Color ForeColor { get; }
        Color TextBoxBackColor { get; }
        Color AccentColor { get; }
    }

    public class LightTheme : ITheme
    {
        public static LightTheme Instance { get; } = new LightTheme();
        public Color BackColor => Color.FromArgb(245, 245, 245);
        public Color ForeColor => Color.FromArgb(33, 33, 33);
        public Color TextBoxBackColor => Color.White;
        public Color AccentColor => Color.FromArgb(0, 120, 215);
    }

    public class DarkTheme : ITheme
    {
        public static DarkTheme Instance { get; } = new DarkTheme();
        public Color BackColor => Color.FromArgb(30, 30, 30);
        public Color ForeColor => Color.FromArgb(240, 240, 240);
        public Color TextBoxBackColor => Color.FromArgb(50, 50, 50);
        public Color AccentColor => Color.FromArgb(100, 180, 255);
    }
    #endregion

    #region ListView Sorter
    public class ListViewItemComparer : IComparer
    {
        private int col;
        private SortOrder order;

        public ListViewItemComparer(int column, SortOrder order)
        {
            col = column;
            this.order = order;
        }

        public int Compare(object x, object y)
        {
            ListViewItem itemX = x as ListViewItem;
            ListViewItem itemY = y as ListViewItem;

            if (itemX == null || itemY == null) return 0;

            // ".." her zaman başta
            if (itemX.Text == "..") return -1;
            if (itemY.Text == "..") return 1;

            bool isXFolder = itemX.SubItems.Count > 2 && itemX.SubItems[2].Text == "Klasör";
            bool isYFolder = itemY.SubItems.Count > 2 && itemY.SubItems[2].Text == "Klasör";

            // Klasörler dosyalardan önce
            if (isXFolder && !isYFolder) return -1;
            if (!isXFolder && isYFolder) return 1;

            int returnVal = 0;

            // Boyut sütununu özel olarak sort et
            if (col == 1 && itemX.SubItems.Count > col && itemY.SubItems.Count > col)
            {
                string xSize = itemX.SubItems[col].Text.Split()[0];
                string ySize = itemY.SubItems[col].Text.Split()[0];
                
                if (double.TryParse(xSize, out double xs) && double.TryParse(ySize, out double ys))
                    returnVal = xs.CompareTo(ys);
                else
                    returnVal = string.Compare(xSize, ySize);
            }
            else if (itemX.SubItems.Count > col && itemY.SubItems.Count > col)
            {
                returnVal = string.Compare(itemX.SubItems[col].Text, itemY.SubItems[col].Text);
            }

            if (order == SortOrder.Descending)
                returnVal *= -1;

            return returnVal;
        }
    }
    #endregion

    #region Settings Management
    public static class AppSettings
    {
        private static readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Emanager",
            "settings.ini");

        public static string LastDirectory { get; set; } = "C:\\";
        public static bool DarkModeEnabled { get; set; } = false;

        public static void Load()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string[] lines = File.ReadAllLines(settingsPath);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("LastDirectory="))
                            LastDirectory = line.Substring(14);
                        else if (line.StartsWith("DarkMode="))
                            DarkModeEnabled = bool.Parse(line.Substring(9));
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Warn("Ayarlar yüklenirken hata: " + ex.Message);
            }
        }

        public static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(settingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var lines = new List<string>
                {
                    $"LastDirectory={LastDirectory}",
                    $"DarkMode={DarkModeEnabled}"
                };

                File.WriteAllLines(settingsPath, lines);
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error("Ayarlar kaydedilirken hata: " + ex.Message);
            }
        }
    }
    #endregion

    #region Input Dialog
    public class InputDialog : Form
    {
        private TextBox inputTextBox;
        private Button okButton;
        private Button cancelButton;
        private Label label;

        public string InputValue { get; set; }
        public string DefaultValue { get; set; }

        public InputDialog()
        {
            this.Text = "Yeni Ad Gir";
            this.Width = 400;
            this.Height = 160;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Segoe UI", 9);

            label = new Label
            {
                Text = "Yeni ad:",
                Left = 20,
                Top = 20,
                Width = 360,
                Height = 25,
                AutoSize = true
            };
            
            inputTextBox = new TextBox
            {
                Left = 20,
                Top = 50,
                Width = 360,
                Height = 30,
                Font = new Font("Segoe UI", 10)
            };
            
            okButton = new Button
            {
                Text = "✓ Tamam",
                Left = 210,
                Top = 90,
                Width = 80,
                Height = 35,
                DialogResult = DialogResult.OK,
                Font = new Font("Segoe UI", 9)
            };
            
            cancelButton = new Button
            {
                Text = "✕ İptal",
                Left = 300,
                Top = 90,
                Width = 80,
                Height = 35,
                DialogResult = DialogResult.Cancel,
                Font = new Font("Segoe UI", 9)
            };

            this.Controls.Add(label);
            this.Controls.Add(inputTextBox);
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            inputTextBox.Text = DefaultValue ?? "";
            inputTextBox.SelectAll();
            inputTextBox.Focus();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (this.DialogResult == DialogResult.OK)
                InputValue = inputTextBox.Text;
        }
    }
    #endregion
}