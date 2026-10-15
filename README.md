# Fluxer

Windows için hızlı, native (Avalonia) arayüzlü indirme yöneticisi: çok bağlantılı HTTP, BitTorrent,
yt-dlp ile video siteleri ve Chromium tarayıcı entegrasyonu.

## Kurulum

1. `artifacts/releases/Fluxer-win-Setup.exe` dosyasını çalıştırın (yönetici yetkisi gerekmez;
   `%LOCALAPPDATA%\Fluxer` altına kurulur, Başlat menüsüne eklenir).
2. Tarayıcı eklentisini yükleyin (Chrome / Edge / Brave / Opera / Vivaldi):
   - `chrome://extensions` (Edge: `edge://extensions`) → **Geliştirici modu** açık
   - **Paketlenmemiş öğe yükle** → `%LOCALAPPDATA%\Fluxer\current\extension`
   - Eklenti kimliği `dckkomckipcaglacbgfmbgmapfckpkja` olmalı (manifest'teki sabit `key` sayesinde).
3. Tarayıcıda bir dosya indirin; indirme Fluxer'a düşer. **Alt** basılıyken tıklarsanız tarayıcıya kalır.

Native messaging kayıtları (Chrome, Edge, Brave, Chromium; Opera/Vivaldi Chrome'unkini okur), `magnet:` ve
`.torrent` ilişkilendirmeleri uygulamanın her açılışında `HKCU` altına yazılır/onarılır.
Durum: **Ayarlar → Entegrasyon**.

## Kullanım ipuçları

| Kısayol | İşlev |
|---|---|
| Ctrl+N | Yeni indirme (çok satırlı yapıştırma desteklenir) |
| Ctrl+V | Panodaki bağlantıları ekle |
| Ctrl+O | .torrent dosyası aç |
| Boşluk / Delete / Enter | Duraklat-sürdür / sil / dosyayı aç |
| Ctrl+Shift+P / R | Tümünü duraklat / sürdür |

- Pencereyi kapatmak tepsiye küçültür; çıkış tepsi menüsünden. Ayarlar: kenar çubuğu → **Ayarlar** (Ctrl+,), değişiklikler anında kaydedilir.
- Kuyruk: satırları Ctrl+↑/↓ ile taşıyın; sütun başlıklarına tıklayarak sıralayın, kenarlarından sürükleyerek genişletin.
- Torrent bağlantılarını tarayıcı indirir, Fluxer torrent olarak açar. Bir site Fluxer'a web sayfası döndürürse indirme otomatik olarak tarayıcıda kalır.
- Süresi dolan bağlantılar "Bağlantı yenilenmeli" durumuna geçer: sayfadan dosyayı tekrar tıklayın
  (Fluxer eşleştirip sorar) ya da sağ tık → **Bağlantıyı yenile**.
- Sayfa yakalayıcı ve video: sayfada sağ tık → **Sayfadaki dosyaları/resimleri yakala** veya
  **Bu sayfayı Fluxer'a gönder**. İlk video indirmesinde yt-dlp, deno ve ffmpeg resmi GitHub
  sürümlerinden indirilir ve SHA-256 ile doğrulanır.

## Geliştirme

Gereksinimler: .NET SDK 10.0.401+, Node 22+, (installer için) Visual Studio C++ araçları (NativeAOT host).

```powershell
dotnet build                                              # tüm çözüm
dotnet test --project tests/Fluxer.Core.Tests             # birim testleri
dotnet test --project tests/Fluxer.Integration.Tests      # Kestrel sahte sunuculu entegrasyon testleri
cd extension; npm ci; npm test; npm run build             # eklenti → extension/dist
$env:FLUXER_INSTANCE="dev"; dotnet run --project src/Fluxer.App   # kurulu sürümün yanında ayrı profil
dotnet run --project src/Fluxer.Cli -- get <url> -c 8     # motor konsol aracı
pwsh build/pack.ps1 -Version 0.6.0                        # installer → artifacts/releases
```

Mimari: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Değişiklikler: [CHANGELOG.md](CHANGELOG.md)

## Otomatik güncelleme

**Ayarlar → Gelişmiş → Güncelleme kaynağı**'na `artifacts/releases` klasörünün yayınlandığı bir URL, ağ
paylaşımı ya da GitHub repo adresi girin. Yeni sürüm arka planda indirilir, çıkışta kurulur.
