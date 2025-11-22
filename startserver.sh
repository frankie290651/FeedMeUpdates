#!/usr/bin/env bash

cd "$( dirname $0 )"
export LD_LIBRARY_PATH=$LD_LIBRARY_PATH:`dirname $0`/RustDedicated_Data/Plugins/x86_64

while true; do
    ./RustDedicated
    sleep 5
    if [ -f "updating.lock" ]; then
        exit 0
    fi
done