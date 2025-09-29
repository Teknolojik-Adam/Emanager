using System;
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
        private string filepack = "C:\\";
        private bool isfile = false;
        private string SelectedItemName = "";
        private int currentSortColumn = 0;
        private SortOrder currentSortOrder = SortOrder.Ascending;

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
                string iconDirectory = Path.Combine(Application.StartupPath, "icons");

                if (!Directory.Exists(iconDirectory))
                {
                    MessageBox.Show($"İkon dizini bulunamadı. Lütfen şuraya oluşturun:\n{iconDirectory}",
                        "İkon Hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                iconlist.ImageSize = new Size(32, 32);
                iconlist.ColorDepth = ColorDepth.Depth32Bit;

                var imageFiles = new[]
                {
                    "archive.png", "doc.png", "exe.png", "folder.png", "gif.png",
                    "jpg.png", "pdf.png", "picture.png", "tex.png", "dll.png",
                    "xls.png", "xml.png", "video.png", "REG.png", "mp3.png",
                    "3gp.png", "aacfile.png", "avatar.png"
                };

                foreach (var file in imageFiles)
                {
                    string filePath = Path.Combine(iconDirectory, file);
                    if (File.Exists(filePath))
                        iconlist.Images.Add(Image.FromFile(filePath));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("İkonlar yüklenemedi: " + ex.Message,
                    "İkon Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            filetexbox.Text = filepack;
            LoadDrives();
        }

        private void LoadDrives()
        {
            comboBox1.Items.Clear();
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            foreach (DriveInfo d in allDrives)
            {
                comboBox1.Items.Add(d.Name);
            }
            if (comboBox1.Items.Count > 0)
                comboBox1.SelectedIndex = 0;
        }

        private async Task loadfile_async()
        {
            try
            {
                if (isfile)
                {
                    OpenSelectedFile();
                    return;
                }

                string currentPath = filepack;
                if (!Directory.Exists(currentPath))
                {
                    MessageBox.Show("Dizin bulunamadı: " + currentPath,
                        "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                toolStripStatusLabel1.Text = "Yükleniyor...";
                listView1.Items.Clear();
                this.Cursor = Cursors.WaitCursor;

                List<ListViewItem> items = await Task.Run(() =>
                {
                    var itemList = new List<ListViewItem>();
                    DirectoryInfo directoryInfo = new DirectoryInfo(currentPath);

                    if (directoryInfo.Parent != null)
                    {
                        var parentItem = new ListViewItem("..");
                        parentItem.SubItems.Add("");
                        parentItem.SubItems.Add("Klasör");
                        parentItem.SubItems.Add("");
                        parentItem.SubItems.Add("");
                        parentItem.ImageIndex = 3;
                        itemList.Add(parentItem);
                    }

                    try
                    {
                        foreach (var dir in directoryInfo.GetDirectories())
                        {
                            var item = new ListViewItem(dir.Name);
                            item.SubItems.Add("");
                            item.SubItems.Add("Klasör");
                            item.SubItems.Add(dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                            item.SubItems.Add(dir.Attributes.ToString());
                            item.ImageIndex = 3;
                            itemList.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException) { }

                    try
                    {
                        foreach (var file in directoryInfo.GetFiles())
                        {
                            string extension = file.Extension.ToUpper();
                            int iconIndex = GetIconIndex(extension);

                            var item = new ListViewItem(file.Name);
                            item.SubItems.Add(FormatFileSize(file.Length));
                            string typeName = extension.Length > 1 ? extension.Substring(1) + " Dosyası" : "Dosya";
                            item.SubItems.Add(typeName);
                            item.SubItems.Add(file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                            item.SubItems.Add(file.Attributes.ToString());
                            item.ImageIndex = iconIndex;
                            itemList.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException) { }

                    return itemList;
                });

                listView1.BeginUpdate();
                listView1.Items.AddRange(items.ToArray());
                listView1.EndUpdate();

                UpdateStatusBar();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dizin yüklenirken hata: {ex.Message}",
                    "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private int GetIconIndex(string extension)
        {
            switch (extension)
            {
                case ".ZIP": case ".RAR": case ".ISO": case ".7Z": return 0;
                case ".DOCX": case ".DOC": return 1;
                case ".EXE": case ".COM": case ".BAT": return 2;
                case ".GIF": return 4;
                case ".JPG": case ".JPEG": return 5;
                case ".PDF": return 6;
                case ".PNG": case ".BMP": case ".TIFF": return 7;
                case ".TEXT": case ".TXT": case ".CS": case ".CPP": return 8;
                case ".DLL": return 9;
                case ".XLS": case ".XLSX": return 10;
                case ".XML": return 11;
                case ".MP4": case ".AVI": case ".MKV": case ".MOV": return 12;
                case ".REG": return 13;
                case ".MP3": case ".MP2": case ".WAV": return 14;
                case ".3GP": return 15;
                case ".AAC": return 16;
                case ".CVS": return 17;
                default: return 7;
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void UpdateStatusBar()
        {
            int folderCount = 0;
            int fileCount = 0;

            foreach (ListViewItem item in listView1.Items)
            {
                if (item.Text == "..") continue;
                if (item.SubItems[2].Text == "Klasör")
                    folderCount++;
                else
                    fileCount++;
            }

            toolStripStatusLabel1.Text = $"{folderCount} klasör, {fileCount} dosya";
            toolStripStatusLabel2.Text = filepack;
        }

        private void OpenSelectedFile()
        {
            if (string.IsNullOrEmpty(SelectedItemName)) return;

            string fullFilePath = Path.Combine(filepack, SelectedItemName);

            if (File.Exists(fullFilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(fullFilePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Dosya açılamadı: {ex.Message}",
                        "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;

                if (selectedName == "..")
                {
                    await gback();
                    return;
                }

                string fullPath = Path.Combine(filepack, selectedName);

                if (Directory.Exists(fullPath))
                {
                    filepack = fullPath;
                    filetexbox.Text = filepack;
                    isfile = false;
                    await loadfile_async();
                }
                else if (File.Exists(fullPath))
                {
                    SelectedItemName = selectedName;
                    OpenSelectedFile();
                }
            }
        }

        private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == currentSortColumn)
            {
                currentSortOrder = (currentSortOrder == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            }
            else
            {
                currentSortColumn = e.Column;
                currentSortOrder = SortOrder.Ascending;
            }

            listView1.ListViewItemSorter = new ListViewItemComparer(e.Column, currentSortOrder);
            listView1.Sort();
        }

        private async void DeleteSelectedItem()
        {
            if (listView1.SelectedItems.Count == 0) return;

            string selectedName = listView1.SelectedItems[0].Text;
            if (selectedName == "..") return;

            string fullPath = Path.Combine(filepack, selectedName);
            string itemType = Directory.Exists(fullPath) ? "klasör" : "dosya";

            var result = MessageBox.Show($"Bu {itemType} silinsin mi?\n{selectedName}",
                "Silme Onayı", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    if (Directory.Exists(fullPath))
                        Directory.Delete(fullPath, true);
                    else
                        File.Delete(fullPath);

                    await loadfile_async();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"{itemType} silinirken hata: {ex.Message}",
                        "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F5:
                    await loadfile_async();
                    break;
                case Keys.Back:
                    await gback();
                    break;
                case Keys.Delete:
                    DeleteSelectedItem();
                    break;
                case Keys.Enter:
                    listView1_DoubleClick(sender, e);
                    break;
            }
        }

        private async void geributton_Click(object sender, EventArgs e)
        {
            await gback();
        }

        private async Task gback()
        {
            try
            {
                string currentPath = filepack;
                if (currentPath.Length <= 3) return;

                string parentPath = Directory.GetParent(currentPath)?.FullName;
                if (Directory.Exists(parentPath))
                {
                    filepack = parentPath;
                    filetexbox.Text = filepack;
                    isfile = false;
                    await loadfile_async();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Geri gidilirken hata: {ex.Message}",
                    "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

     

        private async void loadbuttonaction()
        {
            if (Directory.Exists(filetexbox.Text))
            {
                filepack = filetexbox.Text;
                isfile = false;
                await loadfile_async();
            }
            else
            {
                MessageBox.Show("Dizin mevcut değil!",
                    "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            comboBox1.Visible = checkBox1.Checked;
        }

        private async void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox1.SelectedItem != null)
            {
                string selectedDrive = comboBox1.SelectedItem.ToString();
                filepack = selectedDrive;
                filetexbox.Text = filepack;
                isfile = false;
                await loadfile_async();
            }
        }

        private async void toolStripButtonRefresh_Click(object sender, EventArgs e)
        {
            await loadfile_async();
        }

        private void toolStripButtonDelete_Click(object sender, EventArgs e)
        {
            DeleteSelectedItem();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            listView1_DoubleClick(sender, e);
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DeleteSelectedItem();
        }

        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;
                string fullPath = Path.Combine(filepack, selectedName);
                MessageBox.Show($"{selectedName} özellikleri\nYol: {fullPath}", "Özellikler");
            }
        }

        private void copyPathToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;
                string fullPath = Path.Combine(filepack, selectedName);
                Clipboard.SetText(fullPath);
                MessageBox.Show("Yol panoya kopyalandı: " + fullPath, "Yol Kopyala");
            }
        }

        private void filetexbox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                loadbuttonaction();
            }
        }

        private void listView1_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            if (listView1.SelectedItems.Count > 0)
            {
                string selectedName = listView1.SelectedItems[0].Text;
                string fullPath = Path.Combine(filepack, selectedName);

                dadilab.Text = selectedName;

                if (Directory.Exists(fullPath))
                {
                    dturuetiket.Text = "Klasör";
                }
                else if (File.Exists(fullPath))
                {
                    dturuetiket.Text = Path.GetExtension(fullPath);
                }
                else
                {
                    dturuetiket.Text = "Bilinmiyor";
                }
            }
        }

        private void listView1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var focusedItem = listView1.FocusedItem;
                if (focusedItem != null && focusedItem.Bounds.Contains(e.Location))
                {
                    contextMenuStrip1.Show(listView1, e.Location);
                }
            }
        }

        private class ListViewItemComparer : System.Collections.IComparer
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
                int returnVal = string.Compare(
                    ((ListViewItem)x).SubItems[col].Text,
                    ((ListViewItem)y).SubItems[col].Text);

                if (order == SortOrder.Descending)
                    returnVal *= -1;

                return returnVal;
            }
        }
    }
}