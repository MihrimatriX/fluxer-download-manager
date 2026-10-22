# Proje Prompt'u: "Fluxer" — Hızlı, Şık, Torrent Destekli İndirme Yöneticisi

> Bu prompt bir kodlama ajanı (Claude Code vb.) için yazılmıştır. Proje adı geçicidir; istediğin adla değiştir.

---

## Rol

Sen kıdemli bir .NET masaüstü uygulama mimarısın. Ağ programlama (HTTP/1.1 Range, TCP, connection pooling), Windows dosya I/O performansı (NTFS), BitTorrent protokolü, Avalonia UI ve Chromium Manifest V3 eklentileri konusunda derin deneyimin var. Kodu üretim kalitesinde, test edilebilir ve modüler yazarsın; gereksiz soyutlamadan kaçınırsın.

## Amaç

Yalnızca Windows için, IDM kadar hızlı, modern ve şık **native** arayüzlü, hem HTTP/HTTPS hem BitTorrent indirebilen bir indirme yöneticisi geliştir. Chromium tabanlı tarayıcılardaki (Chrome, Edge, Brave, Opera, Vivaldi) indirmeleri bir tarayıcı eklentisiyle yakalayıp bu uygulamaya aktarsın.

## Alınan Kararlar

| # | Konu | Karar | Gerekçe |
|---|---|---|---|
| 1 | Hedef kitle | Önce kişisel kullanım, sonra yayın | v1'de imzalama/store yok; ama eklenti ID'si sabit, Velopack baştan var |
| 2 | UI | Avalonia (Fluent tema), WebView2/React yok | Native performans, düşük RAM |
| 3 | Platform | Yalnızca Windows, kapı açık | Windows'a özgü kod `Platform/Windows/` altında toplanır; soyutlama katmanı yazılmaz, ihtiyaç doğunca çıkarılır |
| 4 | Process modeli | Tek process (motor + UI) + tray | İndirme hızı aynı; daha az RAM ve ~yarı kod. UI çökmesi resume ile telafi edilir |
| 5 | Torrent | v1'de kesin var | — |
| 6 | Eşzamanlılık | HTTP ve torrent için ayrı limitler (varsayılan 3 + 2); seed eden torrent slot kaplamaz | Yavaş peer bulan torrent HTTP kuyruğunu tıkamaz |
| 7 | Hız limiti | Üç ayrı global limit: HTTP download, torrent download, torrent upload (+ indirme bazlı limit) | İnce kontrol; HTTP tarafı kendi token bucket'ı, torrent tarafı MonoTorrent'in yerleşik limitleri |
| 8 | Link yenileme | Tarayıcıdan otomatik eşleşme (onaylı) + manuel URL yapıştırma | İmzalı CDN linkleri saatler içinde ölür; tekrar tıklamak link kopyalamaktan kolay |
| 9 | Dil | TR + EN, sistem diline göre; ayarlardan değiştirilebilir | Yayın hedefi var; metinleri sonradan koddan sökmek pahalı |
| 10 | Sayfa yakalayıcı + video siteleri | v1'de var, **son aşama** (Aşama 7). Video/playlist çıkarımı yt-dlp ile; extractor yazılmaz | YouTube vb. siteler sık değişir; yt-dlp bunu takip eden olgun araç |
| 11 | Store uyumu | Eklentide YouTube'a özel indirme UI'ı yok; eklenti yalnızca sayfa URL'ini uygulamaya gönderir | Chrome Web Store YouTube indiren eklentileri reddeder |
| 12 | Video indirme | yt-dlp hem çıkarır hem indirir (`--concurrent-fragments`); biz süreci yönetir, ilerlemeyi okuruz | Token/throttling değişikliklerini yt-dlp takip eder; bizim motorla yapmak sık kırılır |
| 13 | yt-dlp/ffmpeg dağıtımı | İlk video indirmesinde resmi kaynaklardan indirilir, SHA-256 doğrulanır | Installer küçük kalır; yt-dlp güncelleyicisi zaten gerekli, aynı kod ilk kurulumu yapar |

## Çalışma Şekli (ÖNEMLİ)

1. Kod yazmadan önce `docs/ARCHITECTURE.md` dosyasında mimariyi, proje yapısını ve aşama planını çıkar; onayımı bekle.
2. Her aşamayı ayrı bir milestone olarak bitir: kod + testler + kısa `CHANGELOG` girdisi. Bir aşamanın kabul kriterleri sağlanmadan sonrakine geçme.
3. Belirsiz bir tasarım kararında (ör. kütüphane seçimi) 2–3 seçeneği artı/eksileriyle kısaca sun, önerini belirt, sonra devam et.
4. Her aşama sonunda: neyin çalıştığını, nasıl test edileceğini ve bilinen eksikleri 5–10 satırda özetle.
5. Kütüphane sürümlerini tahmin etme; güncel kararlı sürümleri kontrol ederek kullan.
6. "Alınan Kararlar" tablosundaki kararları yeniden tartışma; çelişki görürsen sor.

---

## Teknoloji Yığını

| Katman | Teknoloji |
|---|---|
| Çekirdek motor | .NET 10 (LTS), C#, `SocketsHttpHandler`, `RandomAccess` API |
| Torrent | MonoTorrent (doğrudan; ek soyutlama yok — ortak nokta `IDownload`) |
| Kalıcılık | SQLite — `Microsoft.Data.Sqlite` (EF Core yok) |
| UI | Avalonia, Fluent tema, MVVM (kütüphane Aşama 0'da önerilsin) |
| Motor ↔ UI | Aynı process: motor event'leri → UI tarafında ≈4 Hz throttle'lı snapshot |
| Tarayıcı | Manifest V3 eklentisi, TypeScript, Vite ile build (yalnızca Chromium) |
| Tarayıcı ↔ Uygulama | Native Messaging host (küçük .NET konsol exe) → named pipe → uygulama |
| Kurulum / güncelleme | Velopack — native messaging registry kayıtları dahil |
| Test | xUnit, Kestrel ile sahte Range sunucusu (bağlantı başına hız sınırlı); Vitest (eklenti) |

## Process Modeli

- `Fluxer.exe` motoru ve UI'ı birlikte taşır. Pencere kapanınca tray'e iner, indirmeler sürer; çıkış yalnızca tray menüsünden.
- Tek instance: named mutex. İkinci instance (`magnet:`, `.torrent`, dosya ilişkilendirme, `--background`) argümanlarını pipe ile çalışan instance'a iletip çıkar.
- Native host, uygulama çalışmıyorsa `Fluxer.exe --background` ile başlatır (pencere açılmaz, tray'de başlar).
- Motor UI thread'ine hiçbir koşulda bağımlı olmaz (thread pool + async I/O).

## Çözüm Yapısı

```
/src
  Fluxer.Core/            # Domain + HTTP motoru + MonoTorrent adaptörü + kuyruk + SQLite
    Platform/Windows/     # NTFS sparse, uyku engeli, Mark of the Web, registry
  Fluxer.Ipc/             # Pipe mesaj kontratları + istemci (host'un küçük kalması için ayrı)
  Fluxer.App/             # Avalonia UI + tray + pipe sunucusu + tek instance
  Fluxer.NativeHost/      # Native Messaging stdio host (yalnızca Fluxer.Ipc'ye bağımlı)
/extension                # MV3 eklentisi (Chromium)
/tests
  Fluxer.Core.Tests/
  Fluxer.Integration.Tests/
/docs
```

Klasörleri projeye bölmek ancak somut bir ihtiyaç (bağımlılık izolasyonu, derleme süresi) doğunca yapılır.

---

## Fonksiyonel Gereksinimler

### A. HTTP Motoru (projenin kalbi)
- **Çok bağlantılı Range indirme:** Varsayılan 8, ayarlanabilir 1–32 bağlantı. HTTP/1.1'e zorla (HTTP/2 tek bağlantıda multiplex yaptığı için paralellik kaybolur).
- **Dinamik segmentasyon:** Bir segment bitince, kalan baytı en büyük olan segmenti ortadan böl ve boşalan bağlantıya ver. Minimum segment boyutu eşiği olsun (ör. 1 MB). Segment sınırları thread-safe güncellenmeli.
- **Ön ayırma ve doğrudan yazma:** Dosya `.part` uzantısıyla oluşturulsun ve **sparse** olarak işaretlensin (`FSCTL_SET_SPARSE`), sonra `SetLength`. Aksi halde NTFS, VDL'nin ötesine ilk yazmada aradaki alanı senkron sıfırlar ve yazma saniyelerce bloklanır. Segmentler `RandomAccess.WriteAsync(handle, buffer, offset)` ile doğrudan yerine yazılsın; birleştirme adımı yok. Buffer'lar `ArrayPool` ile kiralansın.
- **Disk alanı:** Başlamadan önce boş alan kontrolü; indirme sırasında disk dolarsa temiz duraklatma + bildirim.
- **Range desteklenmiyorsa:** `200` dönerse veya `Accept-Ranges` yoksa tek bağlantıya düş. `Content-Length` yoksa (chunked) tek bağlantı, resume kapalı.
- **Redirect:** Son URL çözülür ve segmentler oraya gider; 403/410'da önce orijinal URL'den yeniden çözülmeyi dene.
- **Adaptif bağlantı sayısı:** Sunucu 429/503 dönerse veya bağlantıyı reddederse o indirmenin bağlantı sayısını azalt.
- **Resume ve çökme tutarlılığı:** Checkpoint sırası: önce veri diske flush edilir (`FlushFileBuffers`), **sonra** segment durumları SQLite'a yazılır (periyodik ≈2 sn ve kapanışta). Devam ederken `If-Range` ile `ETag`/`Last-Modified` doğrulansın. Dosya değiştiyse kullanıcıya sor.
- **Hata toleransı:** Bağlantı bazlı retry (exponential backoff + jitter).
- **Link yenileme:** 403/410 (redirect yeniden çözümü de başarısızsa) → indirme "Link yenilenmeli" durumuna geçer. İki yol:
  - *Otomatik eşleşme:* Eklentiden gelen yeni indirme, bekleyen bir indirmeyle dosya adı + `Content-Length` (varsa `ETag`) üzerinden eşleşirse kullanıcıya "Mevcut indirmeye bağla?" sorulur; onaylanınca yeni URL + yeni cookie/Referer kaydedilip kaldığı yerden devam edilir. Sormadan bağlama yok.
  - *Manuel:* Sağ tık → "Linki yenile" → URL yapıştır. Boyut uyuşmazsa reddedilir, `ETag`/`Last-Modified` değiştiyse uyarılır.
- **Tarayıcı bağlamı:** Cookie, Referer ve User-Agent indirme kaydında saklanıp her istekte gönderilsin.
- **Hız limiti:** HTTP'ye özel global limit + indirme bazlı limit (token bucket). Torrent limitleri ayrıdır (B bölümü).
- **Aynı isimli dosya:** Varsayılan `ad (1).ext`; ayarlardan "üzerine yaz / sor" seçilebilir.
- **Bitişte:** `.part` atomik olarak yeniden adlandırılsın, **Mark of the Web** (`Zone.Identifier`, kaynak URL ile) yazılsın. Opsiyonel hash doğrulama (MD5/SHA-256).
- **Dosya adı tespiti:** `Content-Disposition` (RFC 6266, `filename*` UTF-8 dahil) → URL → MIME tahmini sırasıyla.
- **Uyku engeli:** Aktif indirme varken `SetThreadExecutionState` ile sistem uykusu engellensin.

### B. Torrent
- `.torrent` dosyası ve magnet link desteği; DHT, PEX, uTP açık.
- Torrent içinden dosya seçimi; sıralı indirme seçeneği.
- Seed oranı / süre limiti; torrent için ayrı global download ve upload hız limiti (MonoTorrent'in yerleşik limitleri; ek token bucket yazılmaz).
- `.torrent` dosya ilişkilendirmesi ve `magnet:` protokol kaydı (tek instance'a yönlendirme ile).
- HTTP indirmeleriyle aynı kuyruk ve aynı UI modeli üzerinden yönetilsin (ortak `IDownload` soyutlaması).

### C. Kuyruk ve Kurallar
- Eşzamanlı aktif indirme limiti HTTP ve torrent için ayrı (varsayılan 3 + 2, ayarlanabilir). Seed aşamasındaki torrent slot kaplamaz; seed oranı/süre kurallarına göre arka planda sürer.
- Kategoriler (Video, Müzik, Belge, Arşiv, Program, Torrent) ve uzantı → klasör kuralları.
- Zamanlayıcı: belirli saatte başla/durdur; bitince bilgisayarı uyut/kapat seçeneği.
- Toplu link ekleme (çok satırlı yapıştırma).

### D. Arayüz (Avalonia)
- Modern, minimal, akıcı Fluent görünüm; koyu ve açık tema; sistem temasını takip etsin.
- Ana liste virtualize edilsin (binlerce kayıtta akıcı). Satırda: ad, ikon, ilerleme, hız, kalan süre, durum.
- Detay paneli: segmentlerin görsel haritası (her bağlantının ilerlemesini gösteren özel çizim kontrolü), canlı hız grafiği, HTTP başlıkları, torrent için peer/tracker listesi.
- Motor → UI güncellemeleri ≈4 Hz throttle'lansın; yalnızca görünür satırlar güncellensin.
- Sürükle-bırak (link, `.torrent`), klavye kısayolları.
- Tray ikonu, Windows bildirimleri (indirme bitti/başarısız), küçük "yeni indirme" diyaloğu.
- Ayarlar sayfası: bağlantı sayısı, klasörler, hız limitleri, proxy, dil, tarayıcı entegrasyonu durumu.
- **Dil:** Türkçe ve İngilizce; tüm metinler `.resx` kaynaklarında (kodda sabit metin yok). Varsayılan sistem dili, ayarlardan değiştirilebilir.

### E. Tarayıcı Eklentisi (MV3, Chromium)
- `chrome.downloads.onCreated` ile indirmeyi yakala → `cancel` + `erase` → Native Messaging ile uygulamaya gönder. Küçük dosyada iptal yarışı kaybedilirse sessizce tarayıcıya bırak.
- `blob:`/`data:` URL'leri, POST ile başlayan indirmeler ve küçük dosyalar (ayarlanabilir eşik) tarayıcıya bırakılsın.
- `Alt` basılıyken yakalamayı atla (bypass) — tuş durumu content script ile izlenir (service worker göremez).
- `chrome.cookies.getAll` ile cookie, ayrıca referrer ve user-agent aktarılsın.
- Sağ tık menüsü: "Fluxer ile indir" (Aşama 3); "Sayfadaki dosyaları/resimleri yakala" ve "Bu sayfayı Fluxer'a gönder" (Aşama 7, bkz. F).
- Popup: bağlantı durumu, aktif indirmeler özeti, hızlı ayarlar.
- Eklenti metinleri `_locales/tr` ve `_locales/en` ile (`chrome.i18n`).
- Uygulama kapalıysa / host bulunamazsa indirmeyi tarayıcıya geri bırak ve kullanıcıyı bilgilendir.
- Manifest'te sabit `key` alanı (unpacked geliştirme sırasında ID değişmesin). Store ID'leri (Chrome Web Store ve Edge Add-ons farklı ID verir) yayın aşamasında host manifest'e eklenir.

### F. Sayfa Yakalayıcı ve Video Siteleri (Aşama 7 — en son)
**Sayfa yakalayıcı (eklenti):**
- Content script sayfadaki kaynakları toplar: `<a href>` linkleri, `<img>` (`srcset`'ten en büyük aday, lazy-load için `data-src` vb.), `<video>/<audio>/<source>`, CSS arka plan resimleri. Tekilleştirilir.
- Uygulamada seçim diyaloğu: tür (dosya/resim/medya), uzantı, minimum boyut/çözünürlük, URL deseni ile filtre; toplu seçim; hedef klasör.
- Sayfanın cookie/Referer bağlamı tüm seçilenlere aktarılır. Küçük dosyalar tek bağlantıyla indirilir (segmentleme yalnızca eşik üstünde).

**Video siteleri (uygulama):**
- "Bu sayfayı Fluxer'a gönder" veya uygulamaya URL yapıştırma → yt-dlp ile meta veri çıkarımı → format/kalite seçimi (video+ses, yalnızca ses) → indirme → gerekirse ffmpeg ile birleştirme.
- Playlist/kanal URL'i: öğe listesi gösterilir, seçilenler kuyruğa ayrı indirmeler olarak eklenir.
- **İndirmeyi yt-dlp yapar**, ffmpeg birleştirir; bizim segmentli motor kullanılmaz. Uygulama yt-dlp'yi child process olarak çalıştırır:
  - İlerleme: `--newline` + `--progress-template` ile makine-okunur satırlar parse edilir (yüzde, hız, ETA, fragment).
  - Duraklat = process'i sonlandır; devam = aynı komutla yeniden çalıştır (yt-dlp `.part` üzerinden kaldığı yerden devam eder).
  - Hız limiti: indirmeye `--limit-rate` verilir; global HTTP limiti aktif yt-dlp indirmeleri arasında eşit bölünür (yaklaşık; token bucket paylaşılamaz).
  - Cookie: eklentiden gelen cookie'ler Netscape formatında geçici dosyaya yazılıp `--cookies` ile verilir; dosya yalnızca kullanıcıya açık ACL ile oluşturulur ve indirme bitince silinir.
  - Bu indirmelerde segment haritası yerine tek ilerleme çubuğu + fragment sayacı gösterilir.
  - Process çıktısı/hata kodu loglanır; bilinen hata desenlerinde (ör. "Sign in to confirm", eski sürüm) kullanıcıya anlamlı mesaj.
- **Araç yönetimi:** yt-dlp ve ffmpeg installer'da yok. İlk video indirmesinde (onay diyaloğuyla) resmi GitHub sürümlerinden `%LOCALAPPDATA%\Fluxer\tools\` altına indirilir; yayıncının SHA-256 listesiyle doğrulanır, uyuşmazsa silinip hata verilir. İndirme bizim HTTP motoruyla yapılır.
- Aynı kod güncellemeyi de yapar: yt-dlp sürümü periyodik (ör. haftalık) ve başarısız indirmede kontrol edilir; ayarlarda sürüm + "şimdi güncelle".
- Aşama 7 başında yt-dlp'nin güncel YouTube gereksinimlerini (ör. ek JS runtime ihtiyacı) kontrol et ve araç yönetimine dahil et; tahmin etme.
- Bu indirmeler HTTP eşzamanlılık limitine tabidir ve aynı kuyruk/UI modelinde (`IDownload`) görünür.
- DRM'li içerik kapsam dışı. Kullanıcının indirdiği içerikten kendisi sorumludur (ilk kullanımda kısa uyarı).

### G. Native Messaging Host
- stdio protokolü: 4 byte little-endian uzunluk + UTF-8 JSON; 1 MB mesaj sınırına uy.
- Host yalnızca köprüdür: mesajı doğrular (şema + izin verilen eklenti ID'leri) ve named pipe ile uygulamaya iletir. Uygulama çalışmıyorsa `--background` ile başlatır.
- Her mesajda `protocolVersion` alanı; uyumsuz sürümde eklenti kullanıcıyı güncellemeye yönlendirir.
- Installer şu kayıtları yazar: Chrome, Edge, Brave, Opera, Vivaldi için `HKCU\...\NativeMessagingHosts\<host-adı>` registry anahtarları ve host manifest dosyası.

---

## Fonksiyonel Olmayan Gereksinimler
- **Performans:** 1 Gbps hatta hat hızına yakın indirme; boşta < %1 CPU; pencere açıkken < 150 MB RAM hedefi.
- **Güvenlik:** Localhost'ta açık HTTP portu yok; IPC yalnızca named pipe (kullanıcıya özel ACL). Dosya adları path traversal'a, Windows ayrılmış adlarına (`CON`, `NUL`…) ve sondaki nokta/boşluğa karşı sanitize edilsin; uzun yol (>260) desteklensin. Eklentiden gelen her mesaj doğrulansın.
- **Dayanıklılık:** Uygulama çökse veya elektrik kesilse bile resume çalışmalı (flush → checkpoint sırası).
- **Loglama:** Serilog ile dönen dosya logu; ayarlardan "tanılama paketini dışa aktar".
- **Kod kalitesi:** Nullable açık, analyzer uyarıları hata, `async` her yerde, `CancellationToken` her I/O'da. Domain mantığı UI ve I/O'dan bağımsız ve test edilebilir olsun.

---

## Aşamalar ve Kabul Kriterleri

### Aşama 0 — Mimari
- `docs/ARCHITECTURE.md`: bileşen diyagramı (Mermaid), pipe/Native Messaging mesaj kontratları, veri modeli (SQLite şeması), aşama planı.
- ✅ Benim onayım.

### Aşama 1 — HTTP Motoru (konsol)
- `Fluxer.Core` HTTP kısmı + konsol test uygulaması.
- ✅ Kestrel tabanlı sahte sunucu testleri: Range var/yok, bağlantı kopması, yavaş segment, 200 fallback, ETag değişimi, 429 ile bağlantı azaltma.
- ✅ 1 GB dosyada tek bağlantıya göre ölçülebilir hız artışı. Sahte sunucuda **bağlantı başına hız sınırı** olmalı (localhost'ta sınırsız sunucu hiçbir fark göstermez) veya gerçek uzak sunucu kullanılmalı.
- ✅ Process öldürülüp yeniden başlatılınca kaldığı yerden devam; dosya hash'i doğru.
- ✅ Sparse ön ayırma: son segmentin ilk yazması bloklanmıyor (ölçülmüş).
- ✅ 403 dönen indirme "Link yenilenmeli" durumuna geçiyor; manuel yeni URL ile devam ediyor, boyutu farklı URL reddediliyor.

### Aşama 2 — Kuyruk + Kalıcılık
- Kuyruk, eşzamanlılık limiti, hız limiti, SQLite, event'ler.
- ✅ Entegrasyon testi: 10 indirme kuyruğa, 3 eşzamanlı; durdur/devam/sil.

### Aşama 3 — Minimal UI + Tarayıcı Entegrasyonu
- Avalonia ana liste + yeni indirme diyaloğu + tray + tek instance; MV3 eklenti + Native Host + registry kayıtları.
- ✅ Chrome ve Edge'de normal indirme, giriş gerektiren indirme (cookie) ve sağ tık indirme uygulamaya düşüyor.
- ✅ Uygulama kapalıyken host uygulamayı tray'de başlatıyor.
- ✅ Süresi dolmuş indirme, sayfadan tekrar tıklanınca eşleşiyor; onaydan sonra kaldığı yerden devam ediyor.
- ✅ Tüm UI ve eklenti metinleri kaynak dosyalarında; TR/EN geçişi çalışıyor.

### Aşama 4 — Torrent
- MonoTorrent adaptörü, magnet, dosya seçimi, seed kuralları, dosya/protokol ilişkilendirme.
- ✅ Ubuntu ISO magnet linkiyle uçtan uca indirme ve seed.
- ✅ Torrent peer ararken 3 HTTP indirmesi slot beklemeden çalışıyor (ayrı limitler); seed'e geçen torrent torrent slotunu boşaltıyor.
- ✅ Torrent download/upload limitleri HTTP limitinden bağımsız uygulanıyor.

### Aşama 5 — Tam UI ve Cila
- Detay paneli (segment haritası, hız grafiği, peer listesi), ayarlar, bildirimler, tema, kategoriler, zamanlayıcı.
- ✅ 5.000 kayıtlı listede akıcı kaydırma.
- ✅ Segment haritası ve hız grafiği canlı güncelleniyor.

### Aşama 6 — Dağıtım
- Velopack installer + auto-update (imzasız, kişisel kullanım).
- ✅ Temiz bir Windows'ta tek installer ile her şey kurulup çalışıyor.

### Aşama 7 — Sayfa Yakalayıcı ve Video Siteleri
- Content script kaynak toplama + seçim diyaloğu; yt-dlp + ffmpeg entegrasyonu; playlist desteği; yt-dlp güncelleme.
- ✅ Resim ağırlıklı bir sayfada (ör. galeri) tüm resimler filtrelenip toplu indiriliyor; lazy-load resimler dahil.
- ✅ Bir YouTube videosu seçilen kalitede (ör. 1080p) ses+video birleşik tek dosya olarak iniyor.
- ✅ 20+ öğeli bir YouTube playlist'inden seçilen öğeler kuyruğa düşüp iniyor.
- ✅ yt-dlp indirmesi duraklatılıp uygulama yeniden başlatıldıktan sonra kaldığı yerden devam ediyor; geçici cookie dosyası diskte kalmıyor.
- ✅ Temiz makinede ilk video indirmesi araçları indirip doğruluyor; bozuk hash'li dosya reddediliyor.
- ✅ Eklenti paketinde YouTube'a özel kod/UI yok (store uyumu).

---

## Kapsam Dışı (v1)
- Linux/macOS, Firefox eklentisi
- DRM'li içerik; yt-dlp'nin desteklemediği siteler için kendi HLS/DASH indiricimiz
- Pano izleme, komut paleti
- Kod imzalama, Chrome Web Store / Edge Add-ons yayını

## Açık Sorular
- Yok — planlama kararları tamamlandı. Aşama 0'da çıkan yeni sorular buraya eklenir.

---

## İlk Görevin

Aşama 0'ı yap: `docs/ARCHITECTURE.md` dosyasını oluştur. İçinde şunlar olsun:
1. Mermaid bileşen diyagramı
2. Proje/klasör yapısı
3. `IDownload`, `Segment` gibi temel arayüz ve tiplerin C# taslakları
4. Pipe ve Native Messaging mesaj kontratları (TypeScript + C# tipleri)
5. SQLite şeması
6. Riskler ve açık sorular

Sonra dur ve onayımı bekle.
