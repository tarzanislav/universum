# UNIVERSUM - novy podpisovy kluc vydani (ECDSA P-256), vytvara sa raz (alebo pri vymene kluca).
# Spustite vo Windows PowerShell 5.1 alebo PowerShell 7: cely obsah vlozte do okna PowerShell (je to jeden blok & { }).
# Sukromny kluc sa nikam neuklada: skript ho da do schranky (na poziadanie aj opakovane), vy ho vlozite do GitHubu ako
# tajomstvo UNIVERSUM_RELEASE_KEY prostredia "release". Az potom skript vymaze schranku a vypise verejny kluc - ten nie
# je tajny a patri do src/Universum.Core/Updates/ReleaseSignature.cs. Texty su bez diakritiky (konzola Windows).
& {
    $ErrorActionPreference = 'Stop'
    function FromHex([string]$hex) { [byte[]]($hex -split '(..)' -ne '' | ForEach-Object { [Convert]::ToByte($_, 16) }) }
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $p = $key.ExportParameters($true)
        $point = [byte[]](@([byte]4) + $p.Q.X + $p.Q.Y)
        # SubjectPublicKeyInfo a PKCS#8 kluca P-256: pevne zaciatky DER + bajty kluca
        $public = [byte[]]((FromHex '3059301306072a8648ce3d020106082a8648ce3d030107034200') + $point)
        $private = [byte[]]((FromHex '308187020100301306072a8648ce3d020106082a8648ce3d030107046d306b0201010420') + $p.D + (FromHex 'a144034200') + $point)
        $privateText = [Convert]::ToBase64String($private)
        [Array]::Clear($private, 0, $private.Length)
        [Array]::Clear($p.D, 0, $p.D.Length)
    } finally {
        $key.Dispose()
    }
    do {
        Set-Clipboard -Value $privateText
        Write-Host ''
        Write-Host 'KROK 1: Sukromny kluc je v schranke.' -ForegroundColor Green
        Write-Host 'GitHub: release -> UNIVERSUM_RELEASE_KEY -> ceruzka -> Value: Ctrl+V -> Update secret'
        Write-Host 'Medzitym NIC nekopirujte a nerobte snimku obrazovky (prepisalo by to schranku).' -ForegroundColor Yellow
        $answer = Read-Host 'Ulozene? Napiste H a Enter. Skopirovat znova? Len Enter'
    } until ($answer -match '^[hH]')
    $privateText = $null
    Set-Clipboard -Value ' '
    Write-Host ''
    Write-Host 'KROK 2: Schranka je vymazana. Verejny kluc (poslite ho Claudovi):' -ForegroundColor Cyan
    Write-Host ([Convert]::ToBase64String($public))
    Write-Host ''
}
