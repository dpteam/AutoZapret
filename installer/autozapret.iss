; ============================================================================
;  AutoZapret — Inno Setup script
;
;  Собирается из того же каталога, что и портативный архив (publish\), поэтому
;  установщик и zip — это один и тот же продукт, а не две разные сборки.
;
;  Сборка:
;      powershell -File tools\build-installer.ps1
;  или напрямую:
;      set AUTOZAPRET_VERSION=0.3.0
;      "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\autozapret.iss
;
;  Что даёт установщик, чего не даёт zip:
;    * запись в «Приложения и возможности» и штатное удаление;
;    * ярлыки и автозапуск как выбор, а не как инструкция;
;    * обновление поверх: настройки и свои наборы не трогаются;
;    * при удалении предлагает убрать наш блок из hosts и остановить службу —
;      иначе после удаления остаётся перенаправление, о котором никто не знает.
;
;  Права: установка НЕ требует администратора. Программа работает как asInvoker,
;  а повышение просит только на конкретную операцию (запуск winws, служба,
;  запись hosts). Требовать админа на установку значило бы требовать его зря.
; ============================================================================

#define AppName        "AutoZapret"
#define AppVersion     GetEnv('AUTOZAPRET_VERSION')
#define AppPublisher   "DPT Team"
#define AppUrl         "https://github.com/dpteam/AutoZapret"
#define AppExe         "AutoZapret.exe"
#define ServiceName    "AutoZapret"

#if AppVersion == ""
  #define AppVersion "0.0.0"
#endif

; VersionInfoVersion требует чисто числовой четвёрки: "0.3.0-rc1" ISCC отвергнет.
; Отрезаем суффикс для метаданных exe, а полную строку оставляем в имени
; программы — пререлиз должен выглядеть пререлизом.
#define NumericVersion Copy(AppVersion, 1, Pos('-', AppVersion + '-') - 1)
#if NumericVersion == ""
  #define NumericVersion "0.0.0"
#endif

[Setup]
; AppId — постоянный: по нему Windows отличает «обновить» от «поставить рядом».
; Менять нельзя, иначе обновление поставит вторую копию вместо замены.
AppId={{6C1E4A7B-2D93-4F58-8E1A-3B7C9D2F4E60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Установка для пользователя: без админа, в %LOCALAPPDATA%\Programs.
; Программа и так повышает права только там, где это действительно нужно.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; x64: 32-битного winws не существует, WinDivert64 — единственный драйвер.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts
OutputBaseFilename=AutoZapret-{#AppVersion}-setup
SetupIconFile=..\assets\AutoZapret.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE
; README кладём рядом с exe: человек, поставивший программу без интернета,
; должен иметь объяснение под рукой.
InfoBeforeFile=..\README.md

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart";   Description: "Запускать {#AppName} вместе с Windows (свёрнутым в трей)"; GroupDescription: "Автозапуск:"; Flags: unchecked

; Исключение в Defender. WinDivert64.sys помечен как RiskTool, и Defender
; периодически удаляет его из engine\bin — тогда «обход не работает» без причины.
; Задача НЕ отмечена по умолчанию и говорит, что именно произойдёт: молча
; ослаблять защиту нельзя.
Name: "defender";    Description: "Добавить исключение в Защитник Windows для папки программы (WinDivert помечается как RiskTool и иногда удаляется)"; GroupDescription: "Совместимость:"; Flags: unchecked

[Files]
; Всё, что собрал publish.cmd: exe, WebView2Loader.dll, engine\, assets\, tools\, licenses\.
; Без "skipifsourcedoesntexist": если публикации нет, ISCC падает на сборке
; с внятным путём, а не выпускает установщик, который ничего не ставит.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Иконка рядом с exe: без неё трей рисует уменьшенную копию из exe и выглядит мылом.
Source: "..\assets\AutoZapret.ico"; DestDir: "{app}"; Flags: ignoreversion

; Лицензия нужна и в папке программы: страница лицензии в установщике её
; показывает, но не копирует, а человек должен найти её потом без интернета.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";   Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";   Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Tasks: autostart

[Run]
; Исключение Defender только для папки программы: не для диска, не для системы.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-MpPreference -ExclusionPath '{app}'"""; \
  Flags: runhidden; Tasks: defender

Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Служба держит WinDivert и перехватывает трафик. Если её не остановить,
; удаление файлов упадёт на занятых, а обход останется работать без программы.
Filename: "sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[UninstallDelete]
; Только то, что создали мы сами. Пользовательские данные решаются в [Code].
Type: filesandordirs; Name: "{app}\data\webview2"
Type: filesandordirs; Name: "{app}\data\logs"

[Code]
// ---------------------------------------------------------------------------
// Проверки и уборка. Каждая превращает молчаливую поломку в понятное
// сообщение — либо при установке, либо при удалении.
// ---------------------------------------------------------------------------

function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // .NET 9 требует Windows 10 1607+. Win7/8.1 не поддерживаются: лучше
    // сказать это сейчас, чем упасть на первом запуске.
    if not IsWin64 then
    begin
      MsgBox('{#AppName} требует 64-битную Windows 10 или новее.' + #13#10 +
             '32-битная Windows не поддерживается: 32-битного winws не существует.',
             mbCriticalError, MB_OK);
      Abort;
    end;

    // При обновлении поверх надо сначала погасить программу: иначе exe занят
    // и замена файлов упадёт на «отказано в доступе».
    if IsUpgrade() then
    begin
      Exec('taskkill.exe', '/IM {#AppExe} /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1500);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Сначала гасим программу и службу, потом спрашиваем про данные.
    Exec('taskkill.exe', '/IM {#AppExe} /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Наш блок в hosts пережил бы удаление программы, и человек остался бы
    // с перенаправлением, о котором некому напомнить. Предлагаем убрать.
    if MsgBox('Убрать записи AutoZapret из файла hosts?' + #13#10#13#10 +
              'Программа их туда добавляла. Если оставить, после удаления ' +
              'останется перенаправление на адреса, которых больше нет.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      if FileExists(ExpandConstant('{app}\{#AppExe}')) then
        Exec(ExpandConstant('{app}\{#AppExe}'), '--hosts-restore', '', SW_HIDE,
             ewWaitUntilTerminated, ResultCode);
    end;

    // Настройки и свои наборы — это выбор человека, а не мусор.
    if DirExists(ExpandConstant('{app}\data')) then
    begin
      if MsgBox('Удалить настройки, свои стратегии и наборы hosts?' + #13#10#13#10 +
                ExpandConstant('{app}\data') + #13#10#13#10 +
                'Выберите "Нет", чтобы сохранить их на случай переустановки.',
                mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(ExpandConstant('{app}\data'), True, True, True);
      end;
    end;

    // Данные в %LOCALAPPDATA% появляются, если программа ставилась в
    // Program Files: тогда каталог рядом с exe недоступен для записи.
    if DirExists(ExpandConstant('{localappdata}\AutoZapret')) then
    begin
      if MsgBox('Удалить также данные в ' + ExpandConstant('{localappdata}\AutoZapret') + '?',
                mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(ExpandConstant('{localappdata}\AutoZapret'), True, True, True);
      end;
    end;
  end;
end;
