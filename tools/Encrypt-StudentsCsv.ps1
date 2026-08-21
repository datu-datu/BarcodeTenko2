[CmdletBinding()]
param(
    [string]$InputPath = "data\students.csv",
    [string]$OutputPath = "data\students.enc",
    [string]$PassphrasePath = "data\students.passphrase"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Magic = [System.Text.Encoding]::ASCII.GetBytes("TNKS")
[byte]$Version = 2
$SaltSize = 16
$IvSize = 16
$HmacSize = 32
$KeySize = 32
$Pbkdf2Iterations = 200000

function Fill-RandomBytes {
    param([byte[]]$Buffer)

    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($Buffer)
    }
    finally {
        $rng.Dispose()
    }
}

function Normalize-StudentsCsv {
    param([string]$CsvText)

    $lines = $CsvText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($lines.Count -lt 2) {
        throw "students.csv is empty."
    }

    $header = $lines[0].Trim()
    $rows = New-Object System.Collections.Generic.List[string]
    $rows.Add("student_number,name,code")

    foreach ($line in $lines[1..($lines.Count - 1)]) {
        $parts = $line.Split(",")
        if ($header -eq "student_number,id,name,code") {
            if ($parts.Length -ne 4) {
                throw "Invalid row for legacy header: $line"
            }
            $rows.Add(("{0},{1},{2}" -f $parts[0].Trim(), $parts[2].Trim(), $parts[3].Trim()))
            continue
        }

        if ($header -eq "student_number,name,code") {
            if ($parts.Length -ne 3) {
                throw "Invalid row for current header: $line"
            }
            $rows.Add(("{0},{1},{2}" -f $parts[0].Trim(), $parts[1].Trim(), $parts[2].Trim()))
            continue
        }

        throw "Unsupported header: $header"
    }

    return [string]::Join("`n", $rows)
}

if (-not (Test-Path -LiteralPath $InputPath)) {
    throw "Input file not found: $InputPath"
}

if (-not (Test-Path -LiteralPath $PassphrasePath)) {
    throw "Passphrase file not found: $PassphrasePath"
}

$passphrase = [IO.File]::ReadAllText($PassphrasePath, [Text.Encoding]::UTF8).TrimEnd([char[]]"`r`n")
if ([string]::IsNullOrWhiteSpace($passphrase)) {
    throw "Passphrase file is empty: $PassphrasePath"
}

$plainCsv = Normalize-StudentsCsv -CsvText ([IO.File]::ReadAllText($InputPath, [Text.Encoding]::UTF8))
$plainBytes = [Text.Encoding]::UTF8.GetBytes($plainCsv)

$salt = New-Object byte[] $SaltSize
$iv = New-Object byte[] $IvSize
Fill-RandomBytes -Buffer $salt
Fill-RandomBytes -Buffer $iv

$kdf = [Security.Cryptography.Rfc2898DeriveBytes]::new($passphrase, $salt, $Pbkdf2Iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
$encKey = $kdf.GetBytes($KeySize)
$macKey = $kdf.GetBytes($KeySize)

$aes = [Security.Cryptography.Aes]::Create()
$encryptor = $null
$hmac = $null
try {
    $aes.Mode = [Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $encKey
    $aes.IV = $iv

    $encryptor = $aes.CreateEncryptor()
    $ciphertext = $encryptor.TransformFinalBlock($plainBytes, 0, $plainBytes.Length)

    $payload = New-Object byte[] ($Magic.Length + 1 + $salt.Length + $iv.Length + $ciphertext.Length)
    $offset = 0
    [Array]::Copy($Magic, 0, $payload, $offset, $Magic.Length)
    $offset += $Magic.Length
    $payload[$offset] = $Version
    $offset += 1
    [Array]::Copy($salt, 0, $payload, $offset, $salt.Length)
    $offset += $salt.Length
    [Array]::Copy($iv, 0, $payload, $offset, $iv.Length)
    $offset += $iv.Length
    [Array]::Copy($ciphertext, 0, $payload, $offset, $ciphertext.Length)

    $hmac = [Security.Cryptography.HMACSHA256]::new($macKey)
    $mac = $hmac.ComputeHash($payload)

    $output = New-Object byte[] ($payload.Length + $mac.Length)
    [Array]::Copy($payload, 0, $output, 0, $payload.Length)
    [Array]::Copy($mac, 0, $output, $payload.Length, $mac.Length)

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory | Out-Null
    }

    $tempPath = "$OutputPath.tmp"
    [IO.File]::WriteAllBytes($tempPath, $output)
    Move-Item -LiteralPath $tempPath -Destination $OutputPath -Force
}
finally {
    $aes.Dispose()
    if ($null -ne $encryptor) { $encryptor.Dispose() }
    if ($null -ne $hmac) { $hmac.Dispose() }
    $kdf.Dispose()
}

Write-Host "Encrypted file created: $OutputPath"
