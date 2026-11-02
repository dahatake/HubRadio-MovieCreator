$ErrorActionPreference = 'Continue'
$ff = 'C:\GitHub\HubRadio-MovieCreator\artifacts\e2e-app\Tools\ffmpeg.exe'
$worker = 'C:\GitHub\HubRadio-MovieCreator-run-202611020742\src\HubRadio.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Worker'
$model = 'C:\GitHub\HubRadio-MovieCreator\artifacts\inputs\models\faster-whisper-small'
$ws = Join-Path $env:TEMP 'hrw\ws'
$out = Join-Path $ws 'staging\out'
Remove-Item (Join-Path $env:TEMP 'hrw') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
$sentences = @(
 'Hello and welcome to the radio show.','Today we talk about music and movies.',
 'First, let us talk about the music of the nineteen eighties.','Many listeners love the bright sound of the synthesizer.',
 'Next, we turn to the movies that people watched last summer.','The best film was a quiet story about a small town.',
 'Our guest explains how a good song can change a scene.','She says that silence is as important as sound.',
 'Now we will answer a question from a listener.','The question is about how to choose a movie for the weekend.',
 'Our advice is to start with a story that you already love.','Thank you for listening, and see you next time.')
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$en = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -like 'en*' } | Select-Object -First 1
if ($en) { $s.SelectVoice($en.VoiceInfo.Name) }
$s.Rate = -1
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$wav = Join-Path $ws 'speech.wav'
$s.SetOutputToWaveFile($wav, $fmt)
$s.Speak(($sentences[0..6] -join ' '))
$s.Dispose()
$len = (Get-Item $wav).Length
$dur = [Math]::Round(($len - 44) / 32000, 3).ToString([Globalization.CultureInfo]::InvariantCulture)
"speech seconds: $dur"
$mp4 = Join-Path $ws 'source.mp4'
& $ff -y -f lavfi -i "testsrc=size=1920x1080:rate=30:duration=$dur" -i $wav -c:v mpeg4 -pix_fmt yuv420p -c:a aac -ar 48000 -af apad -t $dur $mp4 2>$null
$analysis = Join-Path $ws 'analysis.wav'
& $ff -nostdin -v error -i $mp4 -map 0:1 -vn -map_metadata -1 -ac 1 -ar 16000 -c:a pcm_s16le -fflags +bitexact -flags:a +bitexact -n $analysis
$bytes = (Get-Item $analysis).Length
$sha = (Get-FileHash $analysis -Algorithm SHA256).Hash.ToLower()
$rid = [guid]::NewGuid().ToString('D')
$req = @{ version = 1; requestId = $rid; method = 'transcribe'; workspaceRoot = $ws; outputRoot = $out
  params = @{ audioPath = $analysis; modelDirectory = $model; inputAudioSha256 = $sha; inputAudioByteSize = $bytes; languagePolicy = 'autoDetectWithJaJpFallback' } } | ConvertTo-Json -Compress -Depth 5
$sw = [Diagnostics.Stopwatch]::StartNew()
Push-Location $out
$meta = $req | & (Join-Path $worker 'python.exe') -I -B -m hubradio_worker --task
Pop-Location
"worker exit=$LASTEXITCODE seconds=$($sw.Elapsed.TotalSeconds)"
$meta
Get-ChildItem $out | Select Name, Length
Copy-Item (Join-Path $out "$rid.json") (Join-Path $env:TEMP 'hrw-asr.json')
Copy-Item $mp4 (Join-Path $env:TEMP 'hrw-source.mp4')
