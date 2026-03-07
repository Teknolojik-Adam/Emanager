using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace teny_desk
{
    public partial class Form1 : Form
    {
        private string currentPath = "C:\\";
        private List<ListViewItem> allItemsCache = new List<ListViewItem>();
        private bool isDarkMode = false;
        private const string searchPlaceholder = "Ara...";

        public Form1()
        {
            InitializeComponent();
            InitializeIconList();
            this.KeyPreview = true;
            this.KeyDown += new KeyEventHandler(Form1_KeyDown);
        }

        private void InitializeIconList()
        {
            try
            {
                string iconDirectory = Path.Combine(Application.StartupPath, "Resources");
                if (!Directory.Exists(iconDirectory))
                {
                    MessageBox.Show($"İkon dizini bulunamadı: {iconDirectory}", "İkon Hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                iconlist.ImageSize = new Size(24, 24);
                iconlist.ColorDepth = ColorDepth.Depth32Bit;
                var imageFiles = Directory.GetFiles(iconDirectory, "*.png");
                foreach (var file in imageFiles)
                {
                    iconlist.Images.Add(Path.GetFileNameWithoutExtension(file), Image.FromFile(file));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("İkonlar yüklenemedi: " + ex.Message, "İkon Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            filetexbox.Text = currentPath;
            txtSearch.Text = searchPlaceholder;
            txtSearch.ForeColor = Color.Gray;
            LoadDrives();
            ApplyTheme(); // İlk temayı uygula
            await LoadFilesAndFoldersAsync();
        }

        #region Theme Management

        private void ApplyTheme()
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
                item.ForeColor = theme.ForeColor;
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
            btnToggleTheme.Text = isDarkMode ? "Açık Mod" : "Karanlık Mod";
            ApplyTheme();
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
                    if (d.IsReady) comboBoxDrives.Items.Add(d.Name);
                }
                if (comboBoxDrives.Items.Count > 0) comboBoxDrives.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sürücüler yüklenirken hata: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadFilesAndFoldersAsync()
        {
            try
            {
                if (!Directory.Exists(currentPath))
                {
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
                    catch (UnauthorizedAccessException) { }
                    try
                    {
                        foreach (var file in dirInfo.GetFiles())
                        {
                            string ext = file.Extension.ToLower().Replace(".", "");
                            string iconKey = iconlist.Images.ContainsKey(ext) ? ext : "picture";
                            var item = new ListViewItem(file.Name, iconKey);
                            item.SubItems.AddRange(new[] { FormatFileSize(file.Length), GetFileTypeName(file.Extension), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                            itemList.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    return itemList;
                });

                listView1.BeginUpdate();
                listView1.Items.Clear();
                allItemsCache.AddRange(items);
                listView1.Items.AddRange(items.ToArray());
                listView1.EndUpdate();
                UpdateStatusBar();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dizin yüklenirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private string GetFileTypeName(string extension) => extension.Length > 1 ? $"{extension.Substring(1).ToUpper()} Dosyası" : "Dosya";
        private string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = (int)Math.Floor(Math.Log(bytes, 1024));
            return $"{Math.Round(bytes / Math.Pow(1024, order), 1)} {sizes[order]}";
        }
        private void UpdateStatusBar()
        {
            int folderCount = allItemsCache.Count(item => item.SubItems[2].Text == "Klasör" && item.Text != "..");
            int fileCount = allItemsCache.Count - folderCount - (allItemsCache.Any(i => i.Text == "..") ? 1 : 0);
            toolStripStatusLabel1.Text = $"{folderCount} klasör, {fileCount} dosya";
            toolStripStatusLabel2.Text = currentPath;
        }

        private async void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;
                if (selectedName == "..") { await GoBackAsync(); return; }

                string fullPath = Path.Combine(currentPath, selectedName);
                if (Directory.Exists(fullPath))
                {
                    currentPath = fullPath;
                    filetexbox.Text = currentPath;
                    await LoadFilesAndFoldersAsync();
                }
                else if (File.Exists(fullPath))
                {
                    try { Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show($"Dosya açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
        }

        private async Task GoBackAsync()
        {
            try
            {
                if (currentPath.Length <= 3) return;
                string parentPath = Directory.GetParent(currentPath)?.FullName;
                if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                {
                    currentPath = parentPath;
                    filetexbox.Text = currentPath;
                    await LoadFilesAndFoldersAsync();
                }
            }
            catch (Exception ex) { MessageBox.Show($"Geri gidilirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void GoToPath()
        {
            if (Directory.Exists(filetexbox.Text))
            {
                currentPath = filetexbox.Text;
                LoadFilesAndFoldersAsync();
            }
            else { MessageBox.Show("Dizin mevcut değil!", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

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
            if (e.KeyCode == Keys.Enter) { GoToPath(); e.SuppressKeyPress = true; }
        }
        private async void geributton_Click(object sender, EventArgs e) => await GoBackAsync();
        private async void toolStripButtonRefresh_Click(object sender, EventArgs e) => await LoadFilesAndFoldersAsync();
        private void openToolStripMenuItem_Click(object sender, EventArgs e) => listView1_DoubleClick(sender, e);
        private async void toolStripButtonDelete_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count == 0 || listView1.SelectedItems[0].Text == "..") return;
            string fullPath = Path.Combine(currentPath, listView1.SelectedItems[0].Text);
            if (MessageBox.Show($"Bu öğe silinsin mi?\n{fullPath}", "Silme Onayı", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try
                {
                    if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true); else File.Delete(fullPath);
                    await LoadFilesAndFoldersAsync();
                }
                catch (Exception ex) { MessageBox.Show($"Silinirken hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
        private void copyPathToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                Clipboard.SetText(Path.Combine(currentPath, listView1.SelectedItems[0].Text));
            }
        }
        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
                MessageBox.Show($"Yol: {Path.Combine(currentPath, listView1.SelectedItems[0].Text)}", "Özellikler");
        }
        private async void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedItem != null)
            {
                currentPath = comboBoxDrives.SelectedItem.ToString();
                filetexbox.Text = currentPath;
                await LoadFilesAndFoldersAsync();
            }
        }
        private async void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) await LoadFilesAndFoldersAsync();
            if (e.KeyCode == Keys.Back) await GoBackAsync();
            if (e.KeyCode == Keys.Delete) toolStripButtonDelete_Click(sender, e);
            if (e.KeyCode == Keys.Enter && listView1.Focused) listView1_DoubleClick(sender, e);
        }
        private void txtSearch_TextChanged(object sender, EventArgs e)
        {
            string searchText = (txtSearch.Text == searchPlaceholder) ? "" : txtSearch.Text.ToLower();
            listView1.BeginUpdate();
            listView1.Items.Clear();
            var filteredItems = string.IsNullOrWhiteSpace(searchText)
                ? allItemsCache
                : allItemsCache.Where(item => item.Text.ToLower().Contains(searchText));
            listView1.Items.AddRange(filteredItems.ToArray());
            listView1.EndUpdate();
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
    }

    public class LightTheme : ITheme
    {
        public static LightTheme Instance { get; } = new LightTheme();
        public Color BackColor => Color.White;
        public Color ForeColor => Color.Black;
        public Color TextBoxBackColor => Color.White;
    }

    public class DarkTheme : ITheme
    {
        public static DarkTheme Instance { get; } = new DarkTheme();
        public Color BackColor => Color.FromArgb(30, 30, 30);
        public Color ForeColor => Color.White;
        public Color TextBoxBackColor => Color.FromArgb(50, 50, 50);
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

            if (itemX.Text == "..") return -1;
            if (itemY.Text == "..") return 1;

            bool isXFolder = itemX.SubItems[2].Text == "Klasör";
            bool isYFolder = itemY.SubItems[2].Text == "Klasör";

            if (isXFolder && !isYFolder) return -1;
            if (!isXFolder && isYFolder) return 1;

            int returnVal = string.Compare(itemX.SubItems[col].Text, itemY.SubItems[col].Text);

            if (order == SortOrder.Descending)
                returnVal *= -1;

            return returnVal;
        }
    }
    #endregion
}