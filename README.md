# teny_desk Dosya Gezgini

`teny_desk`, Windows işletim sistemleri için C# ve WinForms teknolojileri kullanılarak geliştirilmiş, temel dosya yönetimi işlevlerini yerine getiren basit ve hafif bir dosya gezgini uygulamasıdır.

## Özellikler

- **Dosya ve Klasör Listeleme:** Mevcut dizindeki dosya ve klasörleri görüntüler.
- **Sürücü Seçimi:** Sistemdeki sürücüler arasında kolayca geçiş yapma imkanı.
- **Temel Dosya İşlemleri:**
    - Dosya ve klasörleri açma (çift tıklama veya Enter).
    - Seçili dosya veya klasörü silme (Delete tuşu veya sağ tık menüsü).
- **Gezinme:**
    - Bir üst dizine gitme (Geri butonu veya Backspace tuşu).
    - Mevcut dizini yenileme (F5 tuşu veya Yenile butonu).
- **Sıralama:** Sütun başlıklarına tıklayarak dosya ve klasörleri isme, boyuta, türe veya değiştirilme tarihine göre sıralama.
- **Kullanıcı Arayüzü:**
    - Dosya türlerine göre ikonlar.
    - Durum çubuğunda dosya/klasör sayısı ve mevcut yol bilgisi.
    - Sağ tık menüsü ile dosya yolunu kopyalama ve özelliklerini görüntüleme.

## Gereksinimler

- **.NET Framework 4.7.2** veya üstü.
- Projeyi derlemek için **Visual Studio 2019** veya daha yeni bir sürüm.

## Kurulum ve Çalıştırma

1.  Bu repoyu klonlayın veya ZIP olarak indirin.
2.  `teny_desk.sln` dosyasını Visual Studio ile açın.
3.  Proje bağımlılıklarını geri yüklemek için Çözüme sağ tıklayıp "NuGet Paketlerini Geri Yükle" seçeneğini seçin.
4.  **ÖNEMLİ:** Uygulamanın ikonları doğru görüntüleyebilmesi için, projenin çalıştığı `bin/Debug/` (veya `bin/Release/`) dizini içinde `icons` adında bir klasör oluşturmanız gerekmektedir. Projenin kaynak kodunda bulunan `Resources` klasöründeki ikonları (`.png` dosyaları) bu `icons` klasörüne kopyalayın.
5.  Projeyi derlemek ve başlatmak için `F5` tuşuna basın veya "Başlat" düğmesine tıklayın.

## Kullanım

- Uygulama açıldığında varsayılan sürücüdeki dosyaları listeler.
- Üstteki metin kutusuna bir yol yazıp `Enter`'a basarak doğrudan o dizine gidebilirsiniz.
- Sürücü listesini açmak için "Sürücüler" onay kutusunu işaretleyin ve listeden bir sürücü seçin.
- Dosya ve klasörler arasında klavyenizdeki yön tuşları ile gezinebilirsiniz.

### Klavye Kısayolları

- `F5`: Mevcut görünümü yeniler.
- `Enter`: Seçili dosya veya klasörü açar.
- `Backspace`: Bir üst klasöre gider.
- `Delete`: Seçili dosya veya klasörü siler (onay istenir).

## Lisans

Bu proje `LICENSE.txt` dosyasında belirtilen lisans koşulları altında dağıtılmaktadır.
