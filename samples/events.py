"""Print TaskHub events as they happen.

    pip install websockets
    python events.py                 # everything
    python events.py "task.*,reminder.due"
"""
import asyncio
import json
import os
import sys

import websockets  # pip install websockets


def discovery():
    root = os.environ.get("TASKHUB_HOME") or os.path.join(os.environ["LOCALAPPDATA"], "TaskHub")
    with open(os.path.join(root, "api.json"), encoding="utf-8") as f:
        d = json.load(f)
    with open(d["tokenFile"], encoding="utf-8") as f:
        return d["port"], f.read().strip()


async def main(topics):
    port, token = discovery()
    url = f"ws://127.0.0.1:{port}/v1/events?topics={topics}"
    headers = {"Authorization": f"Bearer {token}", "X-TaskHub-Client": "events-py"}
    async with websockets.connect(url, additional_headers=headers) as ws:
        async for message in ws:
            e = json.loads(message)
            task = (e.get("data") or {}).get("task") or {}
            print(f'{e["time"][11:19]}  {e["type"]:<24} {e["source"]:<12} {task.get("title", "")}')


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1] if len(sys.argv) > 1 else "*"))
