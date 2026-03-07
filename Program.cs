using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;

namespace teny_desk
{
    static class Program
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

       
        [STAThread]
        static void Main()
        {
            try
            {
                
                if (System.IO.File.Exists("NLog.config"))
                {
                    LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration("NLog.config");
                }
                logger.Info("=== Emanager Başlatıldı ===");
                
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
                
                logger.Info("=== Emanager Kapatıldı ===");
            }
            catch (Exception ex)
            {
                logger.Fatal("Kritik hata - Uygulama kapatılıyor: " + ex.ToString());
                MessageBox.Show("Uygulama başlatılırken kritik bir hata oluştu.\n\nLütfen hata günlüğünü kontrol edin.",
                    "Kritik Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                LogManager.Shutdown();
            }
        }
    }
}
