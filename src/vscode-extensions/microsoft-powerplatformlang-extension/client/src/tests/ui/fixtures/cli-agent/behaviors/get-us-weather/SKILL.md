---
name: get-us-weather
description: 'Get the current weather (temperature, sky conditions, wind, humidity) for any city or town in the United States. Use when the user asks about the current weather, temperature, or conditions in a US city, e.g. "what''s the weather in Seattle" or "how hot is it in Austin right now".'
argument-hint: 'US city name, optionally with state (e.g. "Portland, OR")'
---

# Get Current US City Weather

Fetches the latest observed weather for a United States city using the free
Open-Meteo geocoding API and the National Weather Service (api.weather.gov),
neither of which requires an API key.

When this skill is activated:

1. Determine the target US city from the user's request. If the name is ambiguous (e.g. "Springfield", "Portland", "Columbus"), ask the user for the state before continuing.
2. Run the helper script [Get-UsWeather.ps1](./scripts/Get-UsWeather.ps1), passing the city (and the state when the user provided one), to look up the coordinates and fetch the latest station observation.
3. Report the current temperature in °F, the sky conditions, humidity, and wind to the user in a short, friendly summary.

## Guidelines

- Only supports locations within the United States — the National Weather Service API does not cover places outside the US.
- Always show the temperature in Fahrenheit for US users; you may add Celsius in parentheses.
- Never fabricate weather values. If the script returns an error or no observation, say so plainly and suggest trying a nearby larger city.
- Pass the state whenever the user gives one (or when the city name is shared by multiple states) so the correct location is selected.
- The script already sets the descriptive `User-Agent` header that api.weather.gov requires — do not remove it.

## Examples

**Example 1: Simple city lookup**
- User request: "What's the weather in Seattle?"
- Expected behavior: Run the script for "Seattle", then reply e.g. "It's currently 62°F and partly cloudy in Seattle, WA, with 71% humidity and a light 5 mph wind."

**Example 2: Ambiguous city name**
- User request: "How's the weather in Portland right now?"
- Expected behavior: Ask "Portland, Oregon or Portland, Maine?", then run the script with the confirmed state before answering.

**Example 3: Non-US city**
- User request: "What's the weather in Paris?"
- Expected behavior: Explain that this skill only covers US cities and that Paris, France is not supported.

## Notes

- "Current" conditions come from the latest station observation, which can lag real time by up to roughly one hour.
- Some stations occasionally report null values; the script automatically checks the next few nearby stations to find valid data.
- Temperature is converted from the API's Celsius values; wind speed is converted from km/h to mph.
- Run the script with, for example: `pwsh -File ./scripts/Get-UsWeather.ps1 -City "Austin" -State "Texas"`.
