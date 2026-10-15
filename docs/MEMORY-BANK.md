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
| devam ediyor (commit edilmedi) | UI yumuşatma + kişiselleştirme, aşağıya bakın |

## Devam eden iş: "yumuşak, hızlı, kişiselleştirilebilir" UI
Kullanıcının isteği: her şey daha yumuşak/soft, hızlı tepkili ve pek çok yeri düzenlenebilir olsun.
Yapılanlar (çalışma kopyasında, commit edilmemiş):
- `UiSettings` (`AppSettings.Ui`): Accent, Mica, Compact, Scale, Animations, ToolbarLabels, SidebarCollapsed,
  DetailVisible/DetailHeight, HiddenColumns/ColumnWidths/ColumnOrder, WindowBounds/Maximized.
- `Services/Appearance.cs`: vurgu rengi (FluentTheme.Palettes), yoğunluk (DensityStyle + `RowHeight` kaynağı),
  Mica (yarı saydam katman fırçaları, Win10'da otomatik opak), `motion` sınıfı, `LayoutTransformControl` ile ölçek.
  `Apply(app, ui)` tüm pencerelere canlı uygular; her yeni pencere `ApplyTo(w, ui)` çağırır.
- `App.axaml`: ControlCornerRadius 8 / Overlay 12, hover geçişleri, ProgressBar/ProgressRing değer animasyonu,
  `.page/.page.hidden` sayfa geçişi, `Border.sidebar` genişlik animasyonu.
- Ana pencere: hamburger ile daralan kenar çubuğu (220↔64), ayarlar ↔ indirmeler kayan geçiş, araç çubuğu etiketleri,
  ayrıntı paneli aç/kapa + hatırlanan yükseklik, sütun başlığına sağ tık (göster/gizle, sıfırla), pencere/sütun yerleşimi kaydı.
- Ayarlar → "Görünüm" bölümü (tema, vurgu rengi örnekleri, Mica, arayüz boyutu %85–130, sıkı yerleşim, animasyonlar, etiketler).
- Hız grafiği: Catmull-Rom eğrisi + solan dolgu.

Doğrulananlar (ekran görüntüsüyle): Mica, sayfa katmanlanması, daraltılmış kenar çubuğu, Görünüm bölümü.
Henüz doğrulanmayan / açık:
- Vurgu rengi örneğine tıklama: son denemede renk uygulanmış görünmedi; `AccentIndex` → `SaveAsync` → `Appearance.Apply`
  zinciri kontrol edilmeli (ListBox SelectedIndex bağlaması tetikleniyor mu?).
- Sıkı yerleşim, ölçek kaydırıcısı, animasyon kapatma, sütun menüsü, yerleşimin yeniden açılışta geri gelmesi canlı denenmedi.
- Hız: Release yayınına `PublishReadyToRun` eklenmesi planlandı, yapılmadı.
- Bitince: testler, CHANGELOG (0.4.0), `build/pack.ps1`, commit.

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
dotnet test --project tests/Fluxer.Core.Tests                      # 34 test
dotnet test --project tests/Fluxer.Integration.Tests               # 20 test (Kestrel sahte sunucu)
cd extension; npm test; npm run build                              # 35 test
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
