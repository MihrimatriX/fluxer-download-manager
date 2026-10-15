# Changelog

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
