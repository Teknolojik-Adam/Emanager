# Emanager Dosya Gezgini v2.0

`Emanager`, Windows işletim sistemleri için C# ve WinForms teknolojileri kullanılarak geliştirilmiş, temel dosya yönetimi işlevlerini yerine getiren basit, hafif ve güçlü bir dosya gezgini uygulamasıdır.

## ✨ Yeni Özellikler (v2.0)

### 📋 Dosya İşlemleri
- **Kopyala** (Ctrl+C) - Dosya ve klasörleri kopyala
- **Taşı** (Ctrl+X) - Dosya ve klasörleri taşı
- **Yapıştır** (Ctrl+V) - Kopyalanan/Taşınan öğeleri yapıştır
- **Yeniden Adlandır** (F2) - Dosya ve klasörleri yeniden adlandır
- Aynı adda dosya varsa otomatik (1), (2) vs. ekleme

### 🔍 Gelişmiş Arama
- **Wildcard desteği:** `*.txt`, `test*`, `?ile.doc` gibi pattern'ler
- **Partial match:** Dosya adının bir kısmını yazarak ara
- Gerçek zamanlı filtreleme

### ⚙️ Kullanıcı Ayarları
- Son kullanılan dizini hatırla
- Tema tercihi (Light/Dark) kaydedilir
- AppData klasöründe güvenli şekilde depolanır
- `C:\Users\<Kullanıcı>\AppData\Roaming\Emanager\settings.ini`

### 🌙 Modern Tema Sistemi
- **Aydınlık Tema:** Temiz, şık tasarım
- **Koyu Tema:** Göz dostu, düşük ışık ortamları için
- Tema seçiminde anında tüm arayüz güncellenir

### 🖥️ Terminal İntegrasyonu
- **Terminali Buradan Aç** - Mevcut konumda CMD açma
- Kolay komut satırı erişimi

### 📊 Logging Sistemi
- **NLog** framework'ü entegre edildi
- Tüm işlemler log dosyasına yazılır
- `C:\Users\<Kullanıcı>\AppData\Roaming\Emanager\logs\`
- Debug seviyesinde ayrıntılı bilgiler

### 🎯 UI/UX İyileştirmeleri
- Daha iyi sürücü görüntüleme (isim + etiket)
- Detaylı dosya özellikleri diyaloğu
- Geliştirilmiş ListView performansı (DoubleBuffered)
- Keyboard shortcut'lar (Ctrl+C, Ctrl+X, Ctrl+V, F2)

## 📌 Mevcut Özellikler

- **Dosya ve Klasör Listeleme:** Mevcut dizindeki dosya ve klasörleri görüntüler.
- **Sürücü Seçimi:** Sistemdeki sürücüler arasında kolayca geçiş yapma imkanı.
- **Temel Dosya İşlemleri:**
    - Dosya ve klasörleri açma (çift tıklama veya Enter)
    - Seçili dosya veya klasörü silme (Delete tuşu veya sağ tık menüsü)
- **Gezinme:**
    - Bir üst dizine gitme (Geri butonu veya Backspace tuşu)
    - Mevcut dizini yenileme (F5 tuşu veya Yenile butonu)
    - Doğrudan yol yazarak navigasyon (Enter)
- **Sıralama:** Sütun başlıklarına tıklayarak dosya ve klasörleri isme, boyuta, türe veya değiştirilme tarihine göre sıralama.
- **Kullanıcı Arayüzü:**
    - Dosya türlerine göre otomatik ikonlar
    - Durum çubuğunda dosya/klasör sayısı ve mevcut yol bilgisi
    - Sağ tık menüsü ile dosya yolunu kopyalama ve özelliklerini görüntüleme.
    - Aydınlık/Koyu tema desteği

## ⌨️ Klavye Kısayolları

| Kısayol | İşlevsellik |
|---------|------------|
| **F5** | Mevcut görünümü yenile |
| **Enter** | Seçili dosya/klasörü aç |
| **Backspace** | Bir üst klasöre git |
| **Delete** | Seçili dosya/klasörü sil |
| **Ctrl+C** | Seçili dosya/klasörü kopyala |
| **Ctrl+X** | Seçili dosya/klasörü taşı |
| **Ctrl+V** | Kopyalanan/Taşınan öğeleri yapıştır |
| **F2** | Seçili dosya/klasörü yeniden adlandır |

## 🔐 Gereksinimler

- **.NET Framework 4.7.2** veya üstü
- Projeyi derlemek için **Visual Studio 2019** veya daha yeni bir sürüm
- **NLog 3.1.0.0** (NuGet - otomatik yüklenir)

## 🚀 Kurulum ve Çalıştırma

1. **Repoyu klonlayın veya ZIP olarak indirin**
   ```bash
   git clone <repo-url>
   ```

2. **Proje dosyasını Visual Studio ile açın**
   ```
   Emanager.sln
   ```

3. **NuGet Paketlerini geri yükleyin**
   - Çözüme sağ tıklayıp "NuGet Paketlerini Geri Yükle" seçin

4. **NLog.config dosyasını ayarlayın** (Önemli!)
   - NLog.config dosyasının "Copy Always" olarak ayarlandığından emin olun
   - Properties seçeneklerinden "Copy to Output Directory" = "Copy Always"

5. **İkonları ayarlayın** (Opsiyonel)
   - Projenin kaynak kodunda bulunan `Resources` klasöründeki `.png` dosyalarını
   - `bin/Debug/` veya `bin/Release/` klasörü altında `Resources` klasörüne kopyalayın
   - Eğer ikonlar yoksa varsayılan sistem ikonları kullanılır

6. **Derleyin ve çalıştırın**
   - `F5` tuşuna basın veya "Başlat" düğmesine tıklayın

## 💾 Dosya Yapısı

```
Emanager/
├── Form1.cs              # Ana form ve temel işlevsellik
├── Form1.Designer.cs     # Form tasarımı ve kontroller
├── Form1.resx            # Form kaynakları
├── Program.cs            # Uygulama giriş noktası (NLog init)
├── Emanager.csproj       # Proje dosyası
├── Emanager.sln          # Solution dosyası
├── App.config            # Uygulama yapılandırması
├── NLog.config           # Logging yapılandırması ⭐ YENİ
├── packages.config       # NuGet paketleri
├── Properties/           # Assembly bilgileri ve kaynaklar
├── Resources/            # İkon dosyaları (opsiyonel .png)
└── LICENSE.txt          # Lisans bilgileri
```

## 📊 Veri Depolama

### Ayarlar
```
%APPDATA%\Emanager\settings.ini
```
İçeriği:
```ini
LastDirectory=C:\Users\...
DarkMode=True/False
```

### Loglar
```
%APPDATA%\Emanager\logs\app-YYYY-MM-DD.log
```

## 🐛 Hata Raporlaması

Hata günlükleri otomatik olarak kaydedilir. Sorun yaşarsanız:
1. `%APPDATA%\Emanager\logs\` klasöründeki log dosyalarını kontrol edin

## 📦 Bağımlılıklar

| Paket | Sürüm | Amaç |
|-------|-------|------|
| **NLog** | 3.1.0 | Loglama framework'ü |


## 📄 Lisans

Bu proje `LICENSE.txt` dosyasında belirtilen lisans koşulları altında dağıtılmaktadır.

---

**Versiyon:** 2.0  
**Son Güncelleme:** Mart 2026  


