## evidence_id

ev_4e3aefeb74af355a71a88a7c106ddd970b3f8501e79e2faeccebf0ec3a2c0d94

## subject.name

screenpipe-app

## subject.digest

| Key    | Value                                                            |
|--------|------------------------------------------------------------------|
| sha256 | 5b2d1b17f4f28ef80c62ac344020c19434ab6a063d09de0ec45c40a37c3dae9e |

## subject.local_path

/Applications/screenpipe.app/Contents/MacOS/screenpipe-app

## subject.format

mach-o

## subject.architecture

arm64

## provider

| Key     | Value                             |
|---------|-----------------------------------|
| id      | native-macos                      |
| name    | macOS native inspection utilities |
| version | null                              |

## analysis_profile

null

## predicate_type

rea.analysis

## operation

inspect_plist

## parameters

| Key | Value |
|-----|-------|


## raw_result

null

## normalized_result.format

xml

## normalized_result.value.CFBundleName

screenpipe

## normalized_result.value.NSHumanReadableCopyright



## normalized_result.value.NSSystemExtensionUsageDescription

This app requires system extension access to capture audio output.

## normalized_result.value.NSCalendarsUsageDescription

screenpipe reads your calendar to detect meetings. Your calendar data stays on-device.

## normalized_result.value.NSScreenCaptureUsageDescription

This app requires screen capture access to record the screen.

## normalized_result.value.NSSupportsAutomaticTermination

true

## normalized_result.value.CFBundleDevelopmentRegion

English

## normalized_result.value.CFBundleVersion

2.7.109

## normalized_result.value.NSCalendarsFullAccessUsageDescription

screenpipe reads your calendar to detect meetings. Your calendar data stays on-device.

## normalized_result.value.CFBundlePackageType

APPL

## normalized_result.value.CFBundleIconFile

icon.icns

## normalized_result.value.CFBundleShortVersionString

2.7.109

## normalized_result.value.NSAppleEventsUsageDescription

screenpipe reads the active tab URL from your browser so captured screens are linked to the page you were on. Your browsing data stays on-device.

## normalized_result.value.NSHighResolutionCapable

true

## normalized_result.value.NSMicrophoneUsageDescription

This app requires microphone access to record audio.

## normalized_result.value.NSAppTransportSecurity

| Key                     | Value |
|-------------------------|-------|
| NSAllowsLocalNetworking | true  |

## normalized_result.value.CFBundleInfoDictionaryVersion

6.0

## normalized_result.value.CFBundleExecutable

screenpipe-app

## normalized_result.value.NSRemindersUsageDescription

screenpipe saves action items it detects from your screen and audio activity to Apple Reminders.

## normalized_result.value.NSSupportsSuddenTermination

true

## normalized_result.value.CFBundleURLTypes

| CFBundleTypeRole | CFBundleURLName        | CFBundleURLSchemes |
|------------------|------------------------|--------------------|
| Editor           | screenpi.pe screenpipe | screenpipe         |

## normalized_result.value.CFBundleIdentifier

screenpi.pe

## normalized_result.value.LSApplicationCategoryType

public.app-category.developer-tools

## normalized_result.value.LSMinimumSystemVersion

10.13

## normalized_result.value.LSRequiresCarbon

true

## normalized_result.value.CGRequestScreenCaptureAccess

This app requires screen capture access to record the screen.

## normalized_result.value.CFBundleDisplayName

screenpipe

## normalized_result.value.ScreenpipeManualHandoffProtocol

1

## normalized_result.value.CSResourcesFileMapped

true

## normalized_result.value.LSEnvironment

| Key                          | Value |
|------------------------------|-------|
| SCREENPIPE_DEV_USE_PROD_DATA | 1     |

## normalized_result.value.NSAudioCaptureUsageDescription

screenpipe captures system audio (via the CoreAudio Process Tap) for local transcription. Specific apps can be excluded by listing their bundle IDs in ~/.screenpipe/audio-exclusions.json.

## normalized_result.bundle

| Key           | Value          |
|---------------|----------------|
| identifier    | screenpi.pe    |
| executable    | screenpipe-app |
| name          | screenpipe     |
| version       | 2.7.109        |
| short_version | 2.7.109        |

## normalized_result.source_path

/Applications/screenpipe.app/Contents/Info.plist

## normalized_result.provenance

| tool   | command                                      | tool_version | version_reason                                                                                                                         | executable_sha256                                                | exit            | stdout_bytes | stderr_bytes |
|--------|----------------------------------------------|--------------|----------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------|-----------------|--------------|--------------|
| file   | /usr/bin/file,-b,$PLIST                      |              | Tool exposes no uniform stable version flag; executable SHA-256 is a prelaunch file sample and does not prove atomic OS image binding. | 8014815e1028410f48966f1880f99c842a5678cf2fcaa010e8d7e9e498f651d0 | [object Object] | 34           | 0            |
| plutil | /usr/bin/plutil,-convert,json,-o,-,--,$PLIST |              | Tool exposes no uniform stable version flag; executable SHA-256 is a prelaunch file sample and does not prove atomic OS image binding. | 5ec63ed0506b7676b30fe69d00ae48c689daf1badc1e786a18166b01b615e8ac | [object Object] | 2069         | 0            |

## normalized_result.limitations



## confidence

observed

## authority

shipped-artifact

## environment

null

## limitations



## relationship_coverage

null

## locations

| kind          | path                                             |
|---------------|--------------------------------------------------|
| artifact-path | /Applications/screenpipe.app/Contents/Info.plist |

## evidence_links

