# Changelog

## Yayınlanmadı

### Yeni
- **Ayna adresleri (HTTP):** yeni indirme penceresinde aynı dosyanın başka adresleri girilebilir (her satıra bir tane; `fluxer-cli get … --mirror URL`). Her ayna yoklanır, yalnızca boyutu tutan ve range destekleyenler kullanılır; bağlantılar asıl adresle aynalar arasında paylaştırılır. Art arda 3 hata veren ya da 403/404/410 döndüren ayna bırakılır, parçaları diğer kaynaklara geçer. Asıl linkin süresi dolsa da sağlam aynalar indirmeyi sürdürür; hiç kaynak kalmazsa "Bağlantı yenilenmeli" olur. Ayrıntı panelinde ayna sayısı ve durumu görünür.
- **Otomatik güncelleme varsayılan olarak açık:** kaynak boşsa Fluxer'ın GitHub sürümleri kullanılır; açılışta ve 6 saatte bir bakılır, indirilen sürüm çıkışta kurulur. Ayarlar → Gelişmiş: "Otomatik güncelleme" ve "Beta sürümler" (GitHub ön sürümleri). 0.8 ayarlarıyla gelen kopyalar da güncelleme almaya başlar.
- **Pano izleme** (İndirmeler, varsayılan kapalı): dosya bağlantısı ya da magnet kopyalanınca yeni indirme penceresi açılır. Web sayfası bağlantıları ve listede zaten olanlar sorulmaz.
- **Torrent izleme klasörü:** klasöre bırakılan `.torrent` dosyaları pencere açmadan eklenir ve `added` alt klasörüne taşınır.
- **Torrent RSS beslemeleri** (Ayarlar → Torrent): RSS 2.0 ve Atom beslemelerindeki yeni torrentler (`.torrent` eki, `.torrent` bağlantısı ya da magnet) belirlenen aralıkta (varsayılan 30 dk) kendiliğinden eklenir. Besleme başına açma/kapama, "yalnızca" ve "atla" regex filtreleri ve kayıt klasörü seçilebilir. Besleme eklendiğinde içinde olanlar indirilmez, yalnızca sonradan yayımlananlar gelir; görülen öğeler besleme başına son 1000 kayıtla hatırlanır. Uygulamanın proxy ayarı kullanılır; yanıtlar 5 MB ile sınırlı, hatalı bir besleme diğerlerini durdurmaz. Tanılama paketinde besleme adreslerinden yalnızca alan adı kalır (passkey sızmasın).
- **Biten indirmeler virüs korumasına gönderilir** (Windows Attachment Services, `IAttachmentExecute`): Mark of the Web yazılır ve Defender/yüklü antivirüs dosyayı tarar; engellenirse bildirim gelir. Torrentler hariç.
- **Çökme sonrası tanılama:** yakalanmamış istisnalar (UI thread'i, `_ = …Async()` görevleri, diğer thread'ler) günlüğe yazılır. Uygulama böyle bir hatayla kapanırsa sonraki açılışta tanılama paketini kaydetmeyi önerir. Hiçbir şey kendiliğinden gönderilmez.

### Düzeltildi
- **Değişmiş dosyaya devam edilmiyor:** link yenilenirken sunucu farklı bir sürüm bildirirse (ETag/Last-Modified) "Yine de devam et" yerine "Baştan başlat" sunulur; önceden iki sürümün baytları tek dosyada birleşip bozuk bir dosya "Tamamlandı" görünebiliyordu. `file-changed` ile duran indirmede "Tekrar dene" baştan başlar.
- İlerleme kaydeden ama sık kopan bir bağlantı ya da range yanıtlarını parça parça gönderen bir CDN indirmeyi artık "Başarısız" yapmıyor.
- Bağlantı limiti için 403 döndüren sunucularda bağlantı sayısı düşer, indirme "Bağlantı yenilenmeli" olmaz. 401 ve imzası dolmuş linkler (400/404) yenileme akışına düşer.
- Yeni link range desteklemiyorsa ya da giriş sayfası (HTML) döndürüyorsa yenilemede yakalanır. 0 baytlık dosyalar iner; dışarıdan kırpılmış `.part` dosyası baştan indirilir.
- Link yenileme başarısız olursa çalışan indirme duraklatılmış kalmıyor.
- Proxy ayarı yt-dlp'ye de uygulanıyor; önceden video trafiği proxy'yi atlıyordu. Ayar açıklaması torrent eşlerinin proxy'den geçmediğini söylüyor.
- **Video araçları kurulumu:** pencerede İptal düğmesi var; 30 saniye veri gelmeyen indirme "yanıt vermiyor" uyarısıyla durur (önceden pencere sonsuza kadar açık kalabiliyordu).
- Zip'ten çıkarılan araçlar önce geçici dosyaya yazılıp sonra yerine taşınıyor; yarım kalan `ffmpeg.exe` artık "kurulu" sayılmıyor ve `ffprobe.exe` de kontrol ediliyor.
- yt-dlp'nin `SHA2-256SUMS` dosyası, yt-dlp'nin GPG imzasıyla koda gömülü anahtara karşı doğrulanıyor (BouncyCastle).
- Araç güncellemeleri GitHub API yerine yayınlanan hash'i `tools.json` kaydıyla karşılaştırıyor; deno ve ffmpeg de güncelleniyor (ffmpeg en fazla 30 günde bir). Aynı anda gelen kontroller tek kontrolde birleşiyor, başarısız video indirmeleri en fazla saatte bir kontrol başlatıyor.
- Hash dosyası ile araç farklı sürümlerden gelirse indirme bir kez yeniden deneniyor; çalışan yt-dlp/ffmpeg güncellenirken eski exe kenara alınıyor.
- yt-dlp'ye deno'nun yolu `--js-runtimes` ile açıkça veriliyor.

### Tarayıcı eklentisi
- **HLS varyantları ana listede birleşiyor:** eklenti ana (master) playlist'i bir kez okuyup kalite ve ses playlist'lerini listeden düşürüyor; satırda en iyi çözünürlük görünüyor ("Akış · 1080p"). Önceden bir video 3–6 satır olarak görünüyordu ve video düğmesi en son gelen playlist'i, çoğu zaman yalnızca ses olanı, yt-dlp'ye gönderiyordu.
- **Segment kirliliği yok:** script'le çekilen numaralı parçalar (CMAF/fMP4 `seg-1.mp4`, `?segment=2` …) seri olarak tanınıp listeden çıkıyor; önceden liste parçalarla dolup asıl akışları 40 satır sınırının dışına itebiliyordu.
- **Gömülü oynatıcılar:** video düğmesi ve sağ tık, videonun kendi çerçevesinin yüklediği akışı seçiyor; bulamazsa yt-dlp'ye üst sayfa yerine gömme adresini (`player.vimeo.com/video/…` gibi) üst sayfa referrer'ıyla gönderiyor. Blob oynatıcıda reklam yerine en büyük video dosyası seçiliyor.
- **Shadow DOM oynatıcılar:** video düğmesi açık ve kapalı shadow root içindeki videolarda da (Reddit gibi web bileşeni oynatıcılar) ve üstüne şeffaf katman konmuş videolarda çıkıyor.
- **İndirmenin geldiği sekme:** yakalama kartı, sekme kimliği ve cookie deposu artık bağlantının tıklandığı sekmeden alınıyor (arka plandaki sekme ya da başka pencere dahil; `target=_blank` için açan sekme). Önceden o an aktif olan sekme kullanılıyordu ve kart yanlış sayfada çıkabiliyordu.
- **"Bu dosya türlerini tarayıcıya bırak"** ayarı (Ayarlar): virgülle ayrılmış uzantılar; ad, adres ve mime türüne bakılır. Web sayfası dönen indirmeler (`text/html`) Fluxer'a sorulmadan tarayıcıda kalıyor (Fluxer zaten geri veriyordu, artık bekleme yok).
- **Daha az iş:** medya listesi bellekte tutuluyor; canlı playlist yenilemesi ya da aynı dosyanın sonraki range isteği depolamaya yazmıyor, rozet yalnızca sayı değişince güncelleniyor, art arda gelen yeni kayıtlar tek yazmada birleşiyor.
- Uçtan uca test (`tools/e2e-extension.mjs`) bunları da kontrol ediyor ve `--headless` ile Linux'ta çalışıyor.
- **Firefox desteği (128+):** aynı kod, ayrı manifest (`dist-firefox/`, arka plan service worker yerine event page). Fluxer her açılışta Firefox'un native messaging kaydını da yazar (`HKCU\Software\Mozilla\NativeMessagingHosts`, `allowed_extensions`); host Firefox'un eklenti kimliği argümanını da doğrular. Kurulum `extension-firefox` klasörünü içerir; `AMO_API_KEY`/`AMO_API_SECRET` sırları varsa CI etiketlerde imzalı `.xpi` üretir. Ayarlar → Tarayıcı entegrasyonu Firefox'u da gösterir.
- `clipboardRead` artık isteğe bağlı izin; yalnızca "Bağlantı yapıştır"a basınca istenir.
- Açılır pencere uygulama ile eklentinin major.minor sürümü farklıysa hangisinin güncellenmesi gerektiğini söyler.
- `npm run store`: mağazalar için `key` alanı çıkarılmış zip (`extension/store/`). CI bunu release'e de ekler.

### Yayın
- `build/pack.ps1` ve CI imzalamayı destekliyor (Azure Artifact Signing ya da signtool parametreleri); secret'lar yoksa imzasız paketler. Tireli etiketler (`v0.9.0-beta.1`) GitHub'da ön sürüm olur. Ayrıntı: `docs/RELEASING.md`.

## 0.8.1 — 2026-10-16

### Düzeltildi
- **Çok dosyalı torrentlerde arayüz donuyordu:** torrent ekleme penceresi ve ayrıntı panelinin Dosyalar sekmesi her dosya için ayrı satır kontrolleri oluşturuyordu. Bu satırlar artık sanallaştırılıyor; yalnızca ekranda görünenler oluşuyor. 5.000 dosyalı bir torrentte ölçülen donmalar: ekleme penceresi 8,3 sn → 0,4 sn, torrent satırını seçmek 4,5 sn → anlık, Dosyalar sekmesi 16,4 sn → anlık. Bellek 1,4 GB → 250 MB.
- Dosya listesi yalnızca Dosyalar sekmesi açıkken kuruluyor ve güncelleniyor; diğer sekmeler bu maliyeti ödemiyor.
- Duraklatılmış torrentlerde `.torrent` dosyası artık her saniye yeniden çözümlenmiyor, bir kez okunup saklanıyor.
- Bir dosyanın işaretini değiştirmek artık tüm dosyaların önceliğini yeniden yazmıyor; yalnızca değişen dosya motora gönderiliyor.

## 0.8.0 — 2026-10-16

Nihai tasarım (`docs/d-m.html`) uygulandı: koyu lacivert degrade, turuncu vurgu, çizgi ikonlar, Segoe UI Variable 13 px.

### Ana pencere
- Üstte 48 px bant: logo, "· N indiriliyor", ortada arama (Ctrl+F), sağda pencere düğmeleri.
- Kenar çubuğu: turuncu **Yeni indirme**, sayılı kategoriler, **Kuyruklar** (Ana kuyruk · N eşzamanlı, Gece kuyruğu · saat · sayı), disk doluluk göstergesi, Ayarlar. Dar kenar çubuğu artık Ayarlar → Görünüm'de ya da Ctrl+B ile.
- Araç çubuğu tek panelde: Devam, Duraklat, Sil, sıra yukarı/aşağı, Torrent ekle, Zamanlayıcı, "N seçili", ayrıntı paneli düğmesi (Ctrl+I).
- Durum hapları sayılarıyla; Tamamlanan seed edenleri de sayar.
- Liste: ikon + ad + TORRENT etiketi, boyut, ilerleme çubuğu + yüzde, hız (seed'de ↑), renkli durum ("4 dk 10 sn kaldı", "Seed · oran 1,42", "Gece kuyruğu · 01:00", "Hata · …"), "Bugün 13:48". Seçili satırda sol kenarda vurgu çizgisi. Dar listede Eklenme sütunu kendiliğinden gizlenir.
- **Ayrıntı paneli sağa taşındı:** büyük yüzde, indirilen/toplam, son 60 sn indirme + yükleme grafiği, segmentli sekmeler (torrent: Bilgi / Dosyalar / Eşler / İzleyici / Seçenekler; HTTP: Bilgi / Bağlantılar / Günlük / Başlıklar), hata kutusu (Bağlantıyı yenile / Tarayıcıda aç), Duraklat–Devam–Yeniden dene–Dosyayı aç, klasör ve bağlantı kopyalama.
- **Sil geri alınabilir:** satırlar listeden hemen kalkar, 6 sn "Geri al" bildirimi; dosyalar diskte kalır. "Dosyalarıyla sil…" bağlam menüsünde. "Bitenleri temizle" de geri alınabilir.
- Durum çubuğu: özet, ↓ / ↑ toplam hız, DHT düğüm sayısı, **hız profili düğmesi** (tıkla → sıradaki profil).

### Yeni özellikler
- **Hız profilleri:** Sınırsız / Dengeli / Arka plan; her biri indirme, yükleme ve eşzamanlı indirme sayısı taşır, Ayarlar → Hız ve zamanlayıcı'da düzenlenir.
- **Haftalık zamanlayıcı:** 7 × 24 ızgara, fırça seçip tıklayarak ya da sürükleyerek boyanır; açıkken saat başı profili değiştirir (elle seçilen profil bir sonraki değişime kadar kalır).
- **Gece kuyruğu:** yeni indirme penceresinde "Gece kuyruğu", listede bağlam menüsünde "Gece kuyruğuna taşı", eklentide sağ tık "Gece kuyruğuna ekle". Belirlenen saatte (varsayılan 01:00) kuyruğa katılır.
- **Tepsi penceresi:** tepsi simgesine sol tık → indirme/yükleme hızı ve mini grafik, bağlantı yapıştırıp ekleme, çalışan indirmeler, hata uyarısı, Tümünü duraklat / Pencereyi aç. Sağ tık menüsü aynı.
- **Torrent ayarları:** dinleme portu, DHT, eş değişimi (PeX), yerel eş keşfi, UPnP/NAT-PMP, şifreleme (tercih et / zorunlu / kapalı), torrent başına en fazla eş.

### Yeni indirme penceresi
- Tasarımdaki düzen: bağlantı kutusunda HTTP / TORRENT / TOPLU etiketi, altında yeşil/uyarı satırı; tek dosyada Boyut / Sürdürme / Tür kutuları, dosya adı, bağlantı sayısı; çoklu bağlantıda seçilebilir liste (yanıt vermeyenler işaretsiz gelir).
- Kayıt yeri + Kuyruk (Ana kuyruk (hemen) / Gece kuyruğu / Duraklatılmış), "Kategori: … · uzantıdan" (tıklayınca değiştirilir), **Bir daha sorma**, **Kuyruğa ekle** ve **İndirmeyi başlat**.

### Ayarlar
- Sol menülü sayfa: Genel, İndirmeler, Hız ve zamanlayıcı, Torrent, Tarayıcı entegrasyonu, Gelişmiş; "‹ İndirmelere dön".
- Görünüm stili listesinde "Lacivert (varsayılan)" ilk sırada; vurgu renklerinin ilki Fluxer turuncusu. Sıkı satırlar 44 px, kapalıyken 54 px.

### Tarayıcı eklentisi
- Açılır pencere tasarıma göre yenilendi: bağlantı durumu, yakalama anahtarı + "Bu sitede kapat", İndirmeler / Bu sayfa sekmeleri, satır düğmeleri, "bugün N dosya gönderildi", Bağlantı yapıştır / Fluxer'ı aç. Ayarlar başlıktaki düğmede.
- **Sayfa içi yakalama bildirimi:** yakalanan indirme için sağ altta kart, 3 sn geri sayım, "Fluxer ile indir" / "Tarayıcıda bırak".
- Sağ tık: "Gece kuyruğuna ekle", "Sayfadaki tüm bağlantıları Fluxer'a gönder". Yeni izin: `clipboardRead` (bağlantı yapıştırma).

### Yükseltme
- 0.7 ayarları ilk açılışta bir kez tasarım varsayılanlarına çekilir: koyu tema, turuncu vurgu, lacivert stil, sıkı satırlar, sütun düzeni sıfırlanır, gece kuyruğu saati 01:00. Sonra istediğiniz gibi değiştirebilirsiniz.

## 0.7.1 — 2026-10-15

### Düzeltildi
- **Başlık alanındaki kontroller tıklanmıyordu:** ☰ (kenar çubuğu) düğmesi ve arama kutusu sürükleme alanının içinde kaldığı için Windows tıklamayı pencere tutma sayıyordu. Bu iki kontrol artık normal tıklama alır (Avalonia `ElementRole="User"`); logo ve boş alan sürüklemeye, çift tıkla büyütmeye devam eder. Windows'un nokta sınıflandırması ve gerçek tıklama/yazma ile doğrulandı.
- Başlık bandı 44 piksele çıkarıldı; ☰, logo, arama kutusu ve pencere düğmeleri aynı yatay eksende ortalı (önceden üst kenara yapışıktı). Arama kutusu biraz genişledi.

## 0.7.0 — 2026-10-15

### Tarayıcı eklentisi (baştan güncellendi)
- **Video yakalama:** sayfada oynayan videolar ve sesler ağ yanıtlarından yakalanır: MP4/WebM/MP3… dosyaları, HLS (`.m3u8`) ve DASH (`.mpd`) akışları. Akış parçaları (`.ts`, `.m4s`), küçük dosyalar ve YouTube'un parça istekleri elenir; aynı dosyanın onlarca aralık isteği tek giriş olur. Simgede sekmedeki video sayısı.
- **Videoların üzerinde "Fluxer ile indir" düğmesi** (fareyle üzerine gelince; tam ekranda da). Normal dosya → doğrudan indirme; blob/MediaSource oynatıcı → yakalanan akış (sayfa referrer olarak gönderilir), yoksa sayfa yt-dlp'ye.
- **Açılır pencere yeniden tasarlandı**, üç sekme:
  - *İndirmeler:* canlı liste (durum etiketi, ilerleme, hız); duraklat / sürdür / dosyayı aç / klasörde göster / listeden kaldır; ada tıklayınca ilerleme penceresi; tümünü duraklat/sürdür; sağ üstten Fluxer'ı aç.
  - *Bu sayfa:* "bu sayfadaki videoyu indir" (yt-dlp), yakalanan medya listesi, **toplu indirme**: sayfadaki dosyalar türüne göre (video, ses, arşiv, belge, program, resim, torrent), arama, tümünü seç → tek ekleme penceresi.
  - *Ayarlar:* yakalama, en küçük boyut, video düğmesi, simge sayacı.
- Sağ tık → "Fluxer ile indir" blob kaynaklı videolarda da çalışır.

### Uygulama
- Yeni mesajlar: `download.batch` (toplu indirme → ekleme penceresi) ve `command` (açılır pencerenin düğmeleri). Durum yanıtı indirme listesini de taşır.
- Yakalanan akışlar için yt-dlp'ye `--referer` (veritabanı migration 3: `media_downloads.referrer`).

### Arayüz düzeltmeleri (kod denetiminden)
- Torrent/video indirmesinde ilerleme penceresi ve ayrıntı paneli gizli bir sekmeyi seçili bırakıp boş içerik gösteriyordu.
- Ekleme penceresinde bağlantı değiştirilince kart eski dosyayı göstermeye devam ediyordu.
- Uzun dosya adları ve durum yazıları "…" ile kırpılmıyordu (listede, eş listesinde, başlıklarda, torrent dosyalarında).
- İngilizce kalan "Delete" ipucu; vurgu rengi ipuçlarında renk kodu yerine renk adı; Türkçe yüzde ("%45,3") ve gün/saat biçimleri.
- Ayrıntı düğmesi seçiliyken düz mavi kare görünüyordu; seçili filtre çipi tüm pencerelerde aynı.
- Kalan çizgiler kaldırıldı: kart kenarı (yalnızca cam stilinde ince parıltı), genişletici kenarlıkları, hata/uyarı bandı kenarları, hız grafiği ızgarası, segment haritası ayırıcıları.
- Neumorfik stilde ekleme penceresi kartları görünmüyordu; saat seçicisi gri kutu kalıyordu.
- Gizli sayfaya Tab ile odak geçebiliyordu; ana pencere her dil/tema değişiminde görünüm modeline abone kalıp sızıyordu.
- Ayrıntı paneli küçük pencerede listeyi tamamen ezebiliyordu; filtre çipleri dar pencerede kırpılıyordu (artık alt satıra geçer); daraltılmış kenar çubuğunda ikonlar aynı hizada.
- Ayarlar başlığı geniş pencerede kartlarla hizasızdı; "Kaydedildi" artık kayboluyor; Escape ayarlardan ve ilerleme penceresinden çıkar.
- İlerleme penceresinde düğme sırası: İptal, Arka plana al, Duraklat (ana eylem en sağda).
- Kaynak yakalama penceresi: seçim düğmeleri sayaç satırında, liste kartta, uzun içerik kayar; açık temada soluk yazılar daha okunaklı.

### Test
- Eklenti: 62 birim testi (medya sınıflandırma, toplu liste, her metin anahtarının iki dilde de olması).
- `tools/e2e-extension.mjs`: geçici Chrome profilinde uçtan uca test (yakalama, simge sayacı, video düğmesi, açılır pencerenin üç sekmesi, toplu seçim; `--send` ile uygulamaya gönderim) — 19/19.
- .NET: 60 test (yeni: referrer kalıcılığı, migration, yt-dlp argümanları, yeni mesajların ayrıştırılması ve durum JSON'u).

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
