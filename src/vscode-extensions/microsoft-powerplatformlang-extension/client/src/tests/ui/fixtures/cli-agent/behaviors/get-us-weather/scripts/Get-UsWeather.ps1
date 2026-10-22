<#
.SYNOPSIS
    Gets the current (latest observed) weather for a city in the United States.

.DESCRIPTION
    Geocodes the given US city with the Open-Meteo geocoding API, resolves the
    National Weather Service grid point, and returns the latest station
    observation (temperature, conditions, humidity, and wind). Both APIs are
    free and require no API key.

.PARAMETER City
    The US city name to look up, e.g. "Seattle".

.PARAMETER State
    Optional full state name or abbreviation to disambiguate the city,
    e.g. "WA" or "Washington".

.EXAMPLE
    ./Get-UsWeather.ps1 -City "Austin" -State "Texas"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $City,

    [Parameter(Position = 1)]
    [string] $State
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Fahrenheit {
    param([double] $Celsius)
    return [math]::Round(($Celsius * 9 / 5) + 32, 1)
}

$headers = @{
    'User-Agent' = 'get-us-weather-skill (weather@example.com)'
    'Accept'     = 'application/geo+json'
}

$query = [uri]::EscapeDataString($City)
$geoUrl = "https://geocoding-api.open-meteo.com/v1/search?name=$query&count=10&language=en&format=json"
$geo = Invoke-RestMethod -Uri $geoUrl -Method Get

$candidates = @($geo.results | Where-Object { $_.country_code -eq 'US' })
if ($State) {
    $stateMatch = @($candidates | Where-Object { $_.admin1 -eq $State -or $_.admin1 -like "*$State*" })
    if ($stateMatch.Count -gt 0) {
        $candidates = $stateMatch
    }
}
if ($candidates.Count -eq 0) {
    throw "Could not find a US city named '$City'."
}
$place = $candidates[0]

$points = Invoke-RestMethod -Uri "https://api.weather.gov/points/$($place.latitude),$($place.longitude)" -Headers $headers
$stations = Invoke-RestMethod -Uri $points.properties.observationStations -Headers $headers

$observation = $null
foreach ($station in @($stations.features | Select-Object -First 5)) {
    $stationId = $station.properties.stationIdentifier
    try {
        $candidate = Invoke-RestMethod -Uri "https://api.weather.gov/stations/$stationId/observations/latest" -Headers $headers
        if ($null -ne $candidate.properties.temperature.value) {
            $observation = $candidate
            break
        }
    }
    catch {
        continue
    }
}
if (-not $observation) {
    throw "No current observations are available for '$City'."
}

$properties = $observation.properties
$temperatureC = $properties.temperature.value
$humidity = $properties.relativeHumidity.value
$windKmh = $properties.windSpeed.value

[pscustomobject]@{
    City         = "$($place.name), $($place.admin1)"
    Conditions   = $properties.textDescription
    TemperatureF = if ($null -ne $temperatureC) { ConvertTo-Fahrenheit $temperatureC } else { $null }
    TemperatureC = if ($null -ne $temperatureC) { [math]::Round($temperatureC, 1) } else { $null }
    HumidityPct  = if ($null -ne $humidity) { [math]::Round($humidity) } else { $null }
    WindMph      = if ($null -ne $windKmh) { [math]::Round($windKmh * 0.621371, 1) } else { $null }
    ObservedUtc  = $properties.timestamp
} | Format-List
