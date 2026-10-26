# UNIVERSUM – nový podpisový kľúč vydaní (ECDSA P-256), vytvára sa raz.
# Spustite na svojom počítači vo Windows PowerShell 5.1 alebo PowerShell 7: celý obsah vložte do okna PowerShell
# (je to jeden blok & { … }, vykoná sa naraz až po poslednej zátvorke).
# Súkromný kľúč sa nikam neukladá: skript ho vloží do schránky, odkiaľ ho vložíte do GitHubu ako tajomstvo
# UNIVERSUM_RELEASE_KEY prostredia "release". Verejný kľúč vypíše – ten nie je tajný a patrí do
# src/Universum.Core/Updates/ReleaseSignature.cs.
& {
    $ErrorActionPreference = 'Stop'
    function FromHex([string]$hex) { [byte[]]($hex -split '(..)' -ne '' | ForEach-Object { [Convert]::ToByte($_, 16) }) }
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $p = $key.ExportParameters($true)
        $point = [byte[]](@([byte]4) + $p.Q.X + $p.Q.Y)
        # SubjectPublicKeyInfo a PKCS#8 kľúča P-256: pevné začiatky DER + bajty kľúča
        $public = [byte[]]((FromHex '3059301306072a8648ce3d020106082a8648ce3d030107034200') + $point)
        $private = [byte[]]((FromHex '308187020100301306072a8648ce3d020106082a8648ce3d030107046d306b0201010420') + $p.D + (FromHex 'a144034200') + $point)
        Set-Clipboard -Value ([Convert]::ToBase64String($private))
        [Array]::Clear($private, 0, $private.Length)
        [Array]::Clear($p.D, 0, $p.D.Length)
    } finally {
        $key.Dispose()
    }
    Write-Host ''
    Write-Host 'Súkromný kľúč je v schránke.' -ForegroundColor Green
    Write-Host 'GitHub: universum → Settings → Environments → release → Add environment secret'
    Write-Host '  Name: UNIVERSUM_RELEASE_KEY    Value: Ctrl+V    → Add secret'
    Write-Host ''
    Write-Host 'Verejný kľúč (nie je tajný – pošlite ho Claudovi):' -ForegroundColor Cyan
    Write-Host ([Convert]::ToBase64String($public))
    Write-Host ''
    Read-Host 'Keď je tajomstvo uložené, stlačte Enter – schránka sa vymaže'
    Set-Clipboard -Value ' '
    Write-Host 'Schránka je vymazaná.'
}
