[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $City,

    [Parameter(Position = 1)]
    [string] $State,

    [ValidateSet('Fahrenheit', 'Celsius', 'Both')]
    [string] $Unit = 'Fahrenheit'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Resolve-UsTemperatureLocation.ps1')

function ConvertTo-Fahrenheit {
    param([double] $Celsius)
    return [math]::Round(($Celsius * 9 / 5) + 32, 1)
}

$headers = @{
    'User-Agent' = 'get-us-temperature-skill (temperature@example.com)'
    'Accept'     = 'application/geo+json'
}

$location = Resolve-UsTemperatureLocation -City $City -State $State
$pointMetadata = Invoke-RestMethod -Uri "https://api.weather.gov/points/$($location.latitude),$($location.longitude)" -Headers $headers
$observationStations = Invoke-RestMethod -Uri $pointMetadata.properties.observationStations -Headers $headers

$latestObservation = $null
foreach ($observationStation in @($observationStations.features | Select-Object -First 5)) {
    $stationIdentifier = $observationStation.properties.stationIdentifier
    try {
        $stationObservation = Invoke-RestMethod -Uri "https://api.weather.gov/stations/$stationIdentifier/observations/latest" -Headers $headers
        if ($null -ne $stationObservation.properties.temperature.value) {
            $latestObservation = $stationObservation
            break
        }
    }
    catch {
        continue
    }
}

if (-not $latestObservation) {
    throw "No current temperature observations are available for '$City'."
}

$temperatureCelsius = [math]::Round($latestObservation.properties.temperature.value, 1)
$temperatureFahrenheit = ConvertTo-Fahrenheit $temperatureCelsius

$displayTemperature = switch ($Unit) {
    'Celsius' { "$temperatureCelsius C" }
    'Both' { "$temperatureFahrenheit F ($temperatureCelsius C)" }
    default { "$temperatureFahrenheit F" }
}

[pscustomobject]@{
    City         = "$($location.name), $($location.admin1)"
    Temperature  = $displayTemperature
    TemperatureF = $temperatureFahrenheit
    TemperatureC = $temperatureCelsius
    ObservedUtc  = $latestObservation.properties.timestamp
} | Format-List