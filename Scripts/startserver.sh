#!/usr/bin/env bash

cd "$( dirname $0 )"
export LD_LIBRARY_PATH=$LD_LIBRARY_PATH:`dirname $0`/RustDedicated_Data/Plugins/x86_64

while true; do
    ./RustDedicated -batchmode -logfile "log.txt" +server.port 28015 +server.level "Procedural Map" +server.seed 1234 +server.worldsize 4000 +server.maxplayers 1 +server.hostname "TestServer1" +server.description My TestServer" +server.identity "testserver" +rcon.port 28016 +rcon.password 1234 +rcon.web 1
    sleep 5
    if [ -f "updating.lock" ]; then
        exit 0
    fi
done
