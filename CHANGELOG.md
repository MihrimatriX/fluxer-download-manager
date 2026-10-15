# Changelog

## 0.6.0 — 2026-10-15

### Çizgisiz, tek parça yüzey
- Kenar çubuğu ile içerik arasındaki dikey çizgi, durum çubuğunun üst çizgisi, araç çubuğu ayırıcıları, liste başlığındaki sütun ayırıcıları ve başlık altı çizgisi kaldırıldı.
- Pencerenin etrafındaki 1 piksellik Windows çerçevesi kaldırıldı (Windows 11).

### Neumorphism
- Ayarlar → Görünüm → **Görünüm stili**: Cam / Mica / **Neumorfik** / Düz.
- Neumorfik: her yüzey aynı taban renginde; kartlar, ayar satırları, butonlar ve filtre çipleri iki yönlü yumuşak gölgeyle kabarık; basılan butonlar, seçili filtre/kategori ve metin kutuları, açılır listeler içe gömük. Açık ve koyu temada ayrı gölge renkleri.

### Düzeltildi
- Düz (ve yeni neumorfik) stilde pencere arka planı boyanmıyordu (siyah görünüyordu).

## 0.5.0 — 2026-10-15

### Liste
- **Onay kutuları:** her satırın başında (fareyle üzerine gelince veya seçiliyken görünür; birden fazla satır seçilince hepsi görünür). Başlıktaki kutu tümünü seçer / temizler, kısmi seçimde "kısmi" durumunu gösterir. Kutular ve seçim her iki yönde senkron.
- Yuvarlatılmış, vurgu renginde yumuşak satır vurgusu; hücrelerdeki noktalı odak çerçevesi kaldırıldı.
- Dosya türüne göre renkli ikon döşemeleri (video mor, müzik pembe, belge mavi, arşiv turuncu, program yeşil, torrent turkuaz).
- Durum renkli etiket olarak (indiriliyor = vurgu rengi, tamamlandı yeşil, hata kırmızı, sırada turuncu, tohumlama mor).
- Liste ve ayrıntı paneli cam kartlarda; seçili durum filtresi vurgu rengiyle dolu.

### Cam (glassmorphism)
- Ayarlar → Görünüm → **Arka plan efekti**: Cam (bulanık, saydam — varsayılan) / Mica / Kapalı. Cam, Windows 11'in sistem akriliğini kullanır: pencerenin arkası bulanık görünür.
- Kartlar, ayar satırları, istatistik kutuları: yarı saydam dolgu + üstten parlayan ince kenar.
- Butonlar basınca hafifçe küçülür.

### Başlık çubuğu
- Küçült/büyüt/kapat düğmeleri artık uygulama tarafından çiziliyor; Windows'un başlık alanında bıraktığı siyah şerit gitti, arka plan efekti en üste kadar kesintisiz. Kapat üzerine gelince kırmızı; büyüt düğmesinde Windows 11 yerleşim menüsü, başlığa çift tıkla büyütme, sürükleyerek yerleştirme çalışıyor. İletişim pencerelerinde yalnızca kapat düğmesi.

## 0.4.1 — 2026-10-15

- **Başlık çubuğu uygulamanın içinde:** Windows'un mavi başlık çubuğu kalktı; kenar çubuğu en üste uzanıyor (☰ ve logo başlıkta), arama kutusu başlığın ortasında. Küçült/büyüt/kapat düğmeleri yerinde; başlıktan sürükleme ve çift tıkla büyütme çalışıyor.
- İlerleme, ekleme ve iletişim pencerelerinde de aynı ince, Mica'lı başlık şeridi.

## 0.4.0 — 2026-10-15

### Daha yumuşak
- Yuvarlatılmış köşeler (kontrol 8, panel/pencere 12), Windows 11 **Mica** arka planı.
- Butonlar, liste satırları ve kartlarda yumuşak hover geçişleri; ilerleme çubukları ve halkalar sıçramak yerine kayarak ilerler; ayarlar ↔ indirmeler arası kayan sayfa geçişi; kenar çubuğu açılıp kapanırken genişler.
- Hız grafiği yumuşak eğri + gradyan dolgu.
- İndirme penceresinin durum rengi ve onay kutuları vurgu rengini izler.

### Daha hızlı
- Sürüm yapıları **ReadyToRun** ile önceden derleniyor: ana pencere açılışı ~3,1 sn → ~1,3 sn (ölçüldü).
- Ayarlar sayfası ilk açılışta oluşturuluyor (açılışta ~0,2 sn kazanç).
- Açılış adımları süreleriyle log'a yazılıyor.

### Düzenlenebilir (hepsi hatırlanır)
- Ayarlar → **Görünüm**: tema, vurgu rengi (Windows rengi + 8 renk), Mica aç/kapa, arayüz boyutu %85–130 (tüm pencereler), sıkı yerleşim, animasyonlar aç/kapa, araç çubuğu etiketleri.
- Daraltılabilir kenar çubuğu (☰), ayrıntı paneli aç/kapa ve yüksekliği.
- Liste sütunları: başlığa sağ tık → göster/gizle, "Sütunları sıfırla"; sürükleyerek sıra ve genişlik.
- Pencere konumu, boyutu ve tam ekran durumu.

### Erişilebilirlik
- Ayar satırlarındaki kontroller ekran okuyucuya satır başlığı ve açıklamasıyla duyuruluyor.

## 0.3.0 — 2026-10-15

### İndirme pencereleri
- **İlerleme penceresi** (her indirme için): durum rengine göre ilerleme halkası, kalın ilerleme çubuğu, anlık hız ve kalan süre; ortalama hız, geçen süre, bağlantı, devam desteği, boyut, sunucu ve kayıt yeri kartları; bağlantı başına renkli segment haritası; bağlantı tablosu (bayt aralığı, alınan, anlık hız, durum); hız grafiği; torrentte eşler, videoda parça sayacı. Duraklat/Sürdür, İptal (dosyayı silme seçeneğiyle), Arka plana al, kompakt mod, "bitince dosyayı aç / klasörü aç / pencereyi kapat". Hata durumunda açıklama ve "Bağlantıyı yenile".
- Pencere tamamlanınca **özet görünüme** geçer (boyut, ortalama hız, süre; Aç / Klasörde göster / Kapat).
- **Tamamlandı kartı:** ilerleme penceresi açık değilken biten indirme için sağ altta küçük kart; 15 sn sonra kapanır, fare üzerindeyken beklemede kalır. Aynı anda tek kart.
- Ayarlar: "Yeni indirmelerde ilerleme penceresi" ve "Tamamlandı kartı" anahtarları. Listede çift tık ve sağ tık → "İlerleme penceresi…" her zaman açar.

### Ekleme penceresi (yeniden yazıldı)
- Tek bağlantıda büyük dosya kartı (türe göre renkli ikon, düzenlenebilir ad, boyut/tür/devam desteği/sunucu); birden çok bağlantıda her biri ayrı yoklanan liste ve toplam boyut.
- Kategori çipleri (Otomatik, Video, Müzik, Belgeler, Arşivler, Programlar, Kategorisiz); otomatikte çoklu eklemede her dosya kendi kategori klasörüne.
- Sürücüdeki boş alan ve "yer yetmiyor" uyarısı; panodan yapıştır; son klasörler menüsü.
- Başlatma: Hemen başlat (kuyruğun başına), Kuyruğa ekle, Duraklatılmış ekle. Gelişmiş: bağlantı sayısı kaydırıcısı, sağlama toplamı.

### Düzeltildi
- Kuyrukta sıralama, tüm indirmeler için sahte "durum değişti" olayı yayınlıyordu; tamamlanmış indirmeler için tekrar bildirim/kart çıkıyordu. Artık ayrı bir `Reordered` olayı var (testli).

## 0.2.0 — 2026-10-15

### Düzeltildi: torrent yerine ".html" inmesi
- Eklenti tarayıcı indirmesini artık **iptal etmiyor, duraklatıyor**. Fluxer adresi 64 baytlık bir yoklamayla kontrol ediyor: gerçek dosya ise alıyor, sunucu Fluxer'a web sayfası (Cloudflare, tek kullanımlık bağlantı, oturum) gönderiyorsa tarayıcı kendi isteğine **kaldığı yerden devam ediyor**; hiçbir şey iki kez istenmiyor.
- `.torrent` dosyalarını (MIME veya ad) tarayıcı indiriyor, Fluxer biten dosyayı diskten okuyup torrent penceresini açıyor. Boyutu bilinen küçük torrentler artık "küçük dosya" kuralına takılmıyor.
- Sunucu tarayıcı User-Agent'ını (TLS parmak izi uyuşmadığı için) reddederse Fluxer kendi UA'sıyla bir kez daha deniyor (gerçek sunucuda görüldü).
- Dosya beklenen bir indirmede HTML gelirse motor dosyayı kaydetmiyor, "got-html" hatasıyla duruyor.

### Arayüz
- **Ayarlar sayfası:** modal yerine uygulama içinde; açıklamalı satırlar, anında kaydetme, hız sınırları için aç/kapa + KB/MB birimi, saat seçicili zamanlayıcı, tarayıcı entegrasyonu durumu + onar, eklenti klasörü/kimliği, log klasörü, ayarları sıfırla.
- **Liste:** DataGrid; sıralanabilir, genişletilebilir, yeri değiştirilebilir sütunlar; "Eklenme" sütunu; sayılı durum filtreleri (Etkin/Sırada/Duraklatılan/Tamamlanan/Hatalı); satır üstü duraklat/sürdür/klasör butonu; kuyrukta yukarı/aşağı/başa/sona taşıma (Ctrl+↑/↓/Home/End) ve "Sırada (2.)" gösterimi; bitenleri temizle; durum çubuğunda hızlı hız sınırı.
- **Torrent:** detayda Dosyalar (canlı seçim, dosya bazında ilerleme) ve Seçenekler (torrent başına indirme/gönderme sınırı, sıralı indirme, kendi paylaşım kuralları).
- **Ekleme penceresi:** adresi yoklayıp ad/boyut/tür/devam ettirilebilirlik gösteriyor; HTML ve torrent uyarısı; tür klasörü önerisi; son klasörler; gelişmiş seçenekler katlanır bölümde.
- Erişilebilirlik: satırlar, kategoriler ve filtreler ekran okuyucuya anlamlı adlarla okunuyor.
- Durum geçişleri loglanıyor (tanılama).
- Geliştirme: `FLUXER_INSTANCE=dev` ile kurulu sürümün yanında ayrı veri/pipe/mutex ile çalışır, registry'ye dokunmaz.

## 0.1.0 — 2026-10-15

### Aşama 0 — Mimari
- `docs/ARCHITECTURE.md`: bileşen diyagramı, kontratlar, SQLite şeması, riskler. uTP kararı: v1'de yok (MonoTorrent desteklemiyor).

### Aşama 1 — HTTP motoru
- Çok bağlantılı Range indirme (1–32, varsayılan 8), HTTP/1.1'e zorlanmış; dinamik segment bölme (en büyük kalanı ortadan böl, 1 MiB eşik).
- `.part` dosyası sparse + ön ayırma, `RandomAccess` ile yerinde yazma, `ArrayPool` tamponlar.
- Checkpoint sırası: konum anlık görüntüsü → `FlushFileBuffers` → SQLite. Çökmede kaldığı yerden devam.
- `If-Range` doğrulaması (ETag/Last-Modified), Range yoksa / chunked ise tek bağlantı.
- 429/503/bağlantı reddinde bağlantı azaltma, `Retry-After`, exponential backoff + jitter, kademeli bağlantı açma (slow start).
- 403/410 → yönlendirmeyi yeniden çözme → "Bağlantı yenilenmeli"; manuel/otomatik yeni bağlantı (boyut kontrolü, doğrulayıcı uyarısı).
- RFC 8305 tarzı IPv6/IPv4 yarışlı bağlanma (bozuk IPv6 rotasında takılmaz).
- RFC 6266 dosya adı, sanitize (traversal, `CON`/`NUL`, uzun ad), `ad (1).ext`, Mark of the Web, MD5/SHA-256 doğrulama, disk dolu algılama, uyku engeli.
- `fluxer-cli` konsol aracı. Testler: Range var/yok, kopma, yavaş segment, 200 fallback, ETag değişimi, 429, chunked, süreç öldürüp devam, sparse ölçümü, hız artışı.

### Aşama 2 — Kuyruk + kalıcılık
- `DownloadQueue`: HTTP (3) ve torrent (2) ayrı slotlar, seed slot kaplamaz; duraklat/devam/sil; kuyruk bitince uyut/kapat.
- SQLite şeması + migration; cookie'ler DPAPI ile şifreli. Global + indirme başına token bucket.

### Aşama 3 — UI + tarayıcı entegrasyonu
- Avalonia 12 (Fluent), tek instance (mutex + pipe), tepsi, TR/EN `.resx`, Windows toast bildirimleri.
- NativeAOT native messaging host (~20 ms tur), `CurrentUserOnly` named pipe, şema + eklenti kimliği doğrulama.
- MV3 eklentisi: yakalama + iptal yarışı, Alt bypass, POST/blob/data/küçük dosya tarayıcıya, cookie/referrer/UA aktarımı, sağ tık menüleri, popup, `_locales` TR/EN.

### Aşama 4 — Torrent
- MonoTorrent 3.0.2: magnet, .torrent, dosya seçimi, sıralı indirme, DHT/PEX/LSD/UPnP, seed oranı/süresi, ayrı hız limitleri, `magnet:`/`.torrent` ilişkilendirme.

### Aşama 5 — Tam UI
- Detay paneli: canlı segment haritası, hız grafiği, HTTP başlıkları, eş ve tracker listesi. Kategoriler, arama, ayarlar, zamanlayıcı, tema, sürükle-bırak, kısayollar, erişilebilir adlar.

### Aşama 6 — Dağıtım
- `build/pack.ps1`: self-contained yayın + AOT host + eklenti → Velopack installer; güncelleme kaynağı ayarı.

### Aşama 7 — Sayfa yakalayıcı ve video
- Sayfa kaynak toplayıcı + filtreli seçim diyaloğu (tür, uzantı, çözünürlük, HEAD ile boyut, desen).
- yt-dlp + deno + ffmpeg araç yönetimi (SHA-256 doğrulama, haftalık güncelleme), kalite seçimi, playlist/kanal, duraklat = süreci sonlandır, devam = `.part`'tan sürdür, geçici cookie dosyası kullanıcıya özel ACL ile ve her durumda silinir, alt süreçler job object ile uygulamayla birlikte kapanır.
