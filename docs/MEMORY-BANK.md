# Memory Bank — kaldığımız yer

Yeni bir oturuma (insan ya da yapay zekâ) başlarken önce bunu, sonra `docs/ARCHITECTURE.md` §10 ve `CHANGELOG.md`'yi okuyun.

## Ürün
Fluxer: Windows için indirme yöneticisi. .NET 10 + Avalonia 12 (Fluent, DataGrid 12.1.2), MonoTorrent 3.0.2,
yt-dlp + deno + ffmpeg (ilk video indirmesinde kurulur), Chromium MV3 eklentisi, NativeAOT native messaging host,
Velopack installer. Kurulum yeri ve veri: `%LOCALAPPDATA%\Fluxer` (`current\` = uygulama, `fluxer.db`, `logs\`, `tools\`).

## Sürümler
| Sürüm | İçerik |
|---|---|
| 0.1.0 | Aşama 0–7 (HTTP motoru, kuyruk, SQLite, UI, eklenti, host, torrent, Velopack, yt-dlp) |
| 0.2.0 | Torrent'in `.html` inmesi düzeltildi (duraklat→yokla→kabul/ret); ayarlar sayfası; DataGrid listesi; torrent sekmeleri |
| 0.3.0 | İlerleme penceresi, tamamlandı kartı, yeniden yazılan ekleme penceresi |
| 0.4.0 | Yumuşak/hızlı/kişiselleştirilebilir UI: Görünüm ayarları (vurgu, Mica, ölçek, sıkı, animasyon, etiketler), daralan kenar çubuğu, sütun menüsü, yerleşim hafızası, ReadyToRun |
| 0.4.1 | Başlık çubuğu uygulamanın içinde |
| 0.5.0 | Liste onay kutuları, renkli ikon/durum etiketleri, cam (sistem akrilik) arka plan, kendi çizdiğimiz başlık düğmeleri |
| 0.6.0 | Çizgisiz yüzey (ayırıcılar, DataGrid çizgileri, pencere çerçevesi yok), neumorfik görünüm stili |
| 0.7.0 | Eklenti: video yakalama + video düğmesi, 3 sekmeli açılır pencere (indirme yönetimi, toplu indirme), `download.batch`/`command` mesajları, medya referrer (migration 3); 35 maddelik UI denetiminin çoğu düzeltildi; uçtan uca eklenti testi |
| 0.7.1 | Başlık bandındaki ☰ ve arama tıklanır (ElementRole=User), 44 px bant |
| 0.8.0 | Nihai tasarım (`docs/d-m.html`): lacivert + turuncu, çizgi ikonlar, sağda ayrıntı paneli, geri alınabilir silme; hız profilleri + haftalık zamanlayıcı, gece kuyruğu (`DownloadState.Scheduled`), tepsi penceresi, torrent ağ ayarları; yeni ekleme penceresi ve sol menülü ayarlar; eklenti açılır penceresi + sayfa içi yakalama kartı |

## Eklenti uçtan uca testi
- `node tools/e2e-extension.mjs`: Chrome 154 `--load-extension`'ı yok sayıyor; betik `--remote-debugging-pipe --enable-unsafe-extension-debugging` + CDP `Extensions.loadUnpacked` kullanır. Geçici profil, ekran dışı pencere, kendi test sunucusu.
- `--send`: Chrome'un `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.fluxer.host` anahtarı dev host'a (FLUXER_INSTANCE=dev ile .bat) işaret etmeli; test sonrası anahtar silinir. Kurulu bir Fluxer kaydı varsa üzerine yazma.
- UI denetiminden kalanlar (bilerek ertelendi): komutlarda CanExecute (seçim yokken araç düğmeleri etkin), satır düğmesinin duruma özgü erişilebilir adı, %130 ölçekte başlık düğmeleri ile arama kutusunun dikey hizası, meşgul pencerelerde kapat düğmesi.

## Doğrulama notları (0.4)
- Vurgu rengi, Mica, ölçek %115, sıkı yerleşim, animasyon kapatma, sütun gizleme + yeniden açılışta geri gelme, kenar çubuğu hafızası canlı denendi (dev profil).
- UIA ile test: SettingRow kontrollerine başlığı ad olarak verir → scratchpad pattern.ps1 -Name "<başlık>" -Action toggle|set.
- Sağ tık otomasyonu pencere odakta değilse menüyü açmaz (önce satıra tıklayın).
- Kullanıcı bilgisayarı kullanırken gerçek fare testleri yapma: scratchpad nctest.ps1 (WM_NCHITTEST + mesajla çift tık) ve UIA desenleri odak çalmadan test eder. Arka planı görmek için screen.ps1 (ekrandan), PrintWindow (shot.ps1) arka plan efektini yakalamaz.

## Önemli kararlar (ayrıntı: ARCHITECTURE §10)
- uTP yok (MonoTorrent desteklemiyor).
- Eklenti indirmeyi iptal etmez, **duraklatır**; uygulama 64 baytlık yoklama yapar. HTML/torrent/ulaşılamaz → ret + `reason`,
  tarayıcı kaldığı yerden devam eder. `.torrent` dosyasını tarayıcı indirir, eklenti `torrentPath` gönderir.
- Yoklama önce tarayıcı UA'sıyla, düşerse Fluxer UA'sıyla (TLS parmak izi / UA tutarsızlığı).
- Dosya beklenen indirmede HTML gelirse motor `got-html` hatasıyla durur.
- Motor → UI ilerlemesi çekme (pull) modeli, 4 Hz; yalnızca çalışan satırlar yenilenir.
- Kuyruk sıralaması `Reordered` olayıyla bildirilir (`StateChanged` değil — sahte bildirim hatası buradan çıktı).

## Ortam tuzakları
- Bu makinede IPv6 bozuk → `HttpEngine.HappyEyeballsAsync`.
- OVH test sunucusu çok bağlantıda IP'yi geçici yasaklar; Hetzner tarayıcı UA'sını düşürür (testte kullanmayın/bilin).
- Git Bash heredoc'u `\\` karakterlerini bozar: kod dosyalarını editörle/Write ile yazın.
- NativeAOT için PATH'te `vswhere` gerekir (`build/pack.ps1` ekliyor). Git Bash `ProgramFiles(x86)` taşımaz → paketlemeyi PowerShell'den çalıştırın.
- Avalonia: alt tipleri eşlemek için seçicide `:is(Control)`; `x:Name` alanları yalnızca `InitializeComponent()` ile dolar;
  gizli görünümün iki yönlü bağlamaları, VM yüklenmeden önce varsayılanları geri yazabilir (VM'yi önce yükleyin);
  stil seçicisinde birleşik seçici (`A, B`) ortak tipte olmayan özelliği çözemez.
- Testler Microsoft Testing Platform ile (`global.json`): `dotnet test --project tests/<proje>`.

## Geliştirme akışı
```powershell
$env:FLUXER_INSTANCE="dev"; dotnet run --project src/Fluxer.App   # %LOCALAPPDATA%\Fluxer-dev, ayrı pipe/mutex, registry'ye dokunmaz
dotnet test --project tests/Fluxer.Core.Tests                      # 37 test
dotnet test --project tests/Fluxer.Integration.Tests               # 26 test (Kestrel sahte sunucu)
cd extension; npm test; npm run build                              # 71 test
node tools/gen-resx.mjs                                            # tools/strings.tsv → Strings.resx + Strings.tr.resx
pwsh build/pack.ps1 -Version x.y.z                                 # artifacts/releases (Setup + delta)
```
- Yeni metin: `tools/strings.tsv` dosyasına `Anahtar<TAB>English<TAB>Türkçe` satırı ekleyin, `node tools/gen-resx.mjs` çalıştırın.
  Kodda olmayan anahtar atlanır; kodda olup tsv'de olmayan anahtar hata verir.
- Geliştirme profilinde indirme klasörünü kullanıcının İndirilenler'i yerine geçici bir klasöre ayarlayın.
- Kurulu sürüm çalışırken geliştirme sürümü `FLUXER_INSTANCE` olmadan çalıştırılmamalı (tek instance kilidi ve registry çakışır).

## Kullanıcı
- Türkçe yazışıyor; soru sorulmadan uçtan uca bitmiş, görülebilir ürün istiyor.
- Arayüzde "şık, yumuşak, hızlı, kişiselleştirilebilir" his arıyor; düz/teknik görünümü yetersiz buluyor.

## Görsel doğrulama (0.8)
- Tasarım dosyası `docs/d-m.html` iç içe paketlenmiş bir HTML: `__bundler/manifest` (base64+gzip) içinde 6 sayfa, her sayfa yine paket. Açıp yerelden sunmak için manifest'i çözüp her sayfanın `template`'ini dosyaya yazın.
- Dev profilini kullanıcının masaüstünü bozmadan sınamak: yerel yavaşlatılmış dosya sunucusu (Range destekli) + `\.\pipe\Fluxer.v1.<SID>.dev` borusuna eklentinin gönderdiği `download.add`/`command` mesajları; ekran görüntüsü PrintWindow (lacivert stil opak olduğu için yeterli), tıklama UIA Invoke ya da pencereye `PostMessage` ile. DataGrid satırları UIA SelectionItem sunmaz → satıra posted tık.
- Tepsi penceresi sınaması: Avalonia tepsi geri çağrısı `WM_USER+1024` (lParam `WM_LBUTTONUP`), işlemin `AvaloniaSimpleWindow` pencerelerinden birine gider.
- Dev ayarlarını uygulama kapalıyken `node:sqlite` ile değiştirmek mümkün (`settings` tablosu, anahtar `app`).
