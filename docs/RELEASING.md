# Yayınlama: imzalama ve eklenti mağazaları

Kod tarafı hazır; aşağıdakiler hesap açmayı ya da ödeme yapmayı gerektirdiği için elle yapılır.

## 1. Kod imzalama (SmartScreen)

`build/pack.ps1` iki yolu destekler, ikisi de yoksa uyarı verip imzasız paketler. vpk, uygulama
exe/dll'lerini (Fluxer.exe, Fluxer.NativeHost.exe dahil) ve `Setup.exe`'yi imzalar ve zaman damgası basar.

### Azure Artifact Signing (önerilen)
1. Azure aboneliğinde bir **Artifact Signing** hesabı ve bir **Public Trust** sertifika profili oluştur
   (kimlik doğrulaması birkaç gün sürebilir; bireysel doğrulamanın ülkenizde açık olup olmadığını kontrol edin).
2. Bir uygulama kaydı (service principal) aç, ona hesap üzerinde *Artifact Signing Certificate Profile Signer* rolünü ver.
3. GitHub → Settings → Secrets and variables → Actions'a ekle:
   `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
   `ARTIFACT_SIGNING_ENDPOINT` (ör. `https://weu.codesigning.azure.net`), `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`.
4. Sonraki etikette CI kendiliğinden imzalar (`release.yml` → "Signing metadata").

Yerelde: `metadata.json` yaz (`Endpoint`, `CodeSigningAccountName`, `CertificateProfileName`), `az login` yap,
`$env:FLUXER_AZURE_SIGN_FILE = "C:\yol\metadata.json"; pwsh build/pack.ps1`.

### Sertifika + signtool
`$env:FLUXER_SIGN_PARAMS = '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /f C:\cert.pfx /p <parola>'`.
Açık kaynak proje için SignPath Foundation da ücretsiz bir seçenek (başvuru gerekir).

## 2. Eklenti mağazaları

`cd extension; npm run build; npm run store` → `extension/store/fluxer-extension-<sürüm>.zip`.
Mağazalar kendi kimliğini verir ve `key` alanını kabul etmez; zip'te `key` yoktur (geliştirme derlemesi
sabit `dckkomckipcaglacbgfmbgmapfckpkja` kimliğini korur). CI her etikette bu zip'i de release'e ekler.

1. **Chrome Web Store**: geliştirici hesabı (tek seferlik kayıt ücreti) → yeni öğe → zip'i yükle.
   **Edge Add-ons**: Partner Center → Microsoft Edge → zip'i yükle (ücretsiz).
2. İzin gerekçeleri (inceleme formuna):
   - `downloads`: tarayıcı indirmesini duraklatıp Fluxer'a devretmek.
   - `nativeMessaging`: yerel Fluxer uygulamasıyla konuşmak.
   - `cookies` + `<all_urls>`: oturum gerektiren indirmelerin çerezlerini Fluxer'a iletmek.
   - `webRequest`: sayfada oynayan video/ses akışlarını bulmak.
   - content script (`all_frames`): videoların üzerindeki "Fluxer ile indir" düğmesi ve 3 sn'lik yakalama kartı.
   - `clipboardRead` isteğe bağlıdır, yalnızca "Bağlantı yapıştır"a basınca istenir.
3. Yayınlanınca mağazanın verdiği kimlikleri `src/Fluxer.Ipc/Protocol.cs` →
   `AllowedExtensionIds` listesine ekle. `ShellIntegration` native host manifestinin `allowed_origins`'ini
   her açılışta bu listeden yazar; uygulama güncellemesiyle kullanıcılara ulaşır.
4. README'deki "Load unpacked" adımını mağaza bağlantılarıyla değiştir.

### Firefox (addons.mozilla.org)

`npm run store` Firefox için ayrıca `extension/store/fluxer-extension-firefox-<sürüm>.zip` üretir. Firefox'un
kararlı sürümü imzasız eklentiyi kalıcı kurmaz; iki yol var:

- **Listelenmemiş (önerilen başlangıç):** AMO'da geliştirici hesabı (ücretsiz) → Tools → Manage API Keys →
  depoya `AMO_API_KEY` (JWT issuer) ve `AMO_API_SECRET` (JWT secret) sırlarını ekle. Bundan sonra her `v*`
  etiketinde CI `web-ext sign --channel unlisted` ile imzalı `.xpi` üretip release'e ekler. Aynı sürüm AMO'ya
  iki kez yüklenemez; etiketi yeniden basmadan önce `manifest.json` → `version`'ı artır.
- **Listelenmiş:** zip'i AMO'ya elle yükle; inceleme sonrası mağazada görünür. İzin gerekçeleri yukarıdakiyle aynı.

Eklenti kimliği her iki yolda da `fluxer@fluxer-download-manager` kalır (`Protocol.AllowedFirefoxIds`), yani
uygulama tarafında değişiklik gerekmez.

## 3. Sürüm ve kanallar

- `git tag v1.1.0 && git push origin v1.1.0` → kararlı sürüm; kurulu kopyalar birkaç saat içinde indirir, çıkışta kurar.
- `v1.1.0-beta.1` gibi tire içeren etiketler GitHub'da **pre-release** olur; yalnızca Ayarlar → Gelişmiş →
  "Beta sürümler" açık olan kopyalar alır.
- Eklenti sürümünü (`extension/public/manifest.json` → `version`) uygulamayla aynı major.minor'da tut;
  açılır pencere fark olunca hangi tarafın güncellenmesi gerektiğini söyler.
