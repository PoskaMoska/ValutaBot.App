# Use .NET SDK to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# We replace net10.0 with net9.0 in Docker just in case the container only has 9.0 stable
RUN sed -i 's/<TargetFramework>net10.0<\/TargetFramework>/<TargetFramework>net9.0<\/TargetFramework>/g' ValutaBot.App.csproj
RUN dotnet publish "ValutaBot.App.csproj" -c Release -o /app/out

# Build the runtime image with ASP.NET and Python
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Install Python3, pip, and OpenMP (required for LightGBM on Linux)
RUN apt-get update && \
    apt-get install -y python3 python3-pip python3-venv libgomp1 && \
    rm -rf /var/lib/apt/lists/*

# Copy the compiled .NET application
COPY --from=build /app/out .

# Copy the Python ML Service
COPY ml_service ./ml_service

# Setup Python Virtual Environment and install dependencies
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip3 install --no-cache-dir -r ./ml_service/requirements.txt

# Open the port
EXPOSE 5000 8765

# Start the bot (the .NET app will automatically launch Python via Process.Start)
ENTRYPOINT ["dotnet", "ValutaBot.App.dll"]
