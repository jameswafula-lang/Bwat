# Bwat server for Render (or any Docker host).
# The server is the same C# code as on Windows. On Linux it runs on Mono and uses
# Mono's built-in SQLite provider (compiled with the MONO symbol, see Database.cs).
FROM ubuntu:24.04

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      mono-devel libmono-sqlite4.0-cil libsqlite3-0 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY BwatCommon ./BwatCommon
COPY BwatServer ./BwatServer

# Compile with C# 5 - exactly the language version SharpDevelop uses.
RUN mcs -langversion:5 -target:library -out:BwatCommon.dll BwatCommon/Protocol.cs \
 && mcs -langversion:5 -define:MONO -out:BwatServer.exe -r:BwatCommon.dll -r:Mono.Data.Sqlite -r:System.Data \
        BwatServer/*.cs \
 && rm -rf BwatCommon BwatServer

# Render tells the server which port to use in the PORT variable (normally 10000).
ENV PORT=10000
EXPOSE 10000
CMD ["mono", "BwatServer.exe"]
