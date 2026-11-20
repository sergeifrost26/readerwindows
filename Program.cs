name: Build EXE
on:
  push:
  workflow_dispatch:
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet publish ScreenReader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o package
      - name: Download Piper and voices
        shell: pwsh
        run: |
          $ProgressPreference = 'SilentlyContinue'
          Invoke-WebRequest "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" -OutFile piper.zip
          Expand-Archive piper.zip -DestinationPath package
          New-Item -ItemType Directory -Force package/voices | Out-Null
          $base = "https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU"
          foreach ($v in @("irina", "denis")) {
            try {
              Invoke-WebRequest "$base/$v/medium/ru_RU-$v-medium.onnx.json" -OutFile "package/voices/ru_RU-$v-medium.onnx.json"
              Invoke-WebRequest "$base/$v/medium/ru_RU-$v-medium.onnx" -OutFile "package/voices/ru_RU-$v-medium.onnx"
            } catch {
              Write-Host "skip $v"
              Remove-Item "package/voices/ru_RU-$v-medium.*" -ErrorAction SilentlyContinue
            }
          }
      - uses: actions/upload-artifact@v4
        with:
          name: ScreenReader
          path: package
