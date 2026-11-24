import datetime
import json
import os
import uuid

import azure.functions as func
from azure.data.tables import TableServiceClient

app = func.FunctionApp(http_auth_level=func.AuthLevel.ANONYMOUS)
TABLE_NAME = "PoHillKingDailyScores"


def _table():
    conn = os.environ["AzureWebJobsStorage"]
    svc = TableServiceClient.from_connection_string(conn)
    svc.create_table_if_not_exists(TABLE_NAME)
    return svc.get_table_client(TABLE_NAME)


@app.route(route="daily-score", methods=["POST"])
def post_daily_score(req: func.HttpRequest) -> func.HttpResponse:
    try:
        body = req.get_json()
    except ValueError:
        return func.HttpResponse("Invalid JSON", status_code=400)

    date = str(body.get("date") or datetime.datetime.utcnow().strftime("%Y-%m-%d"))[:10]
    player = str(body.get("player") or "Player")[:24].strip() or "Player"
    champion = str(body.get("champion") or "?")[:32].strip()
    score = max(0, min(999999, int(body.get("score") or 0)))
    duration = max(0.0, min(600.0, float(body.get("durationSec") or 0.0)))

    entity = {
        "PartitionKey": date,
        "RowKey": f"{999999 - score:06d}-{int(duration * 100):05d}-{uuid.uuid4().hex[:8]}",
        "player": player,
        "champion": champion,
        "score": score,
        "durationSec": duration,
    }
    _table().upsert_entity(entity)
    return func.HttpResponse(json.dumps({"ok": True}), mimetype="application/json")


@app.route(route="daily-leaderboard", methods=["GET"])
def get_daily_leaderboard(req: func.HttpRequest) -> func.HttpResponse:
    date = (req.params.get("date") or datetime.datetime.utcnow().strftime("%Y-%m-%d"))[:10]
    rows = list(_table().query_entities(f"PartitionKey eq '{date}'"))[:20]
    entries = [
        {
            "date": date,
            "player": r.get("player", "Player"),
            "champion": r.get("champion", "?"),
            "score": int(r.get("score", 0)),
            "durationSec": float(r.get("durationSec", 0.0)),
        }
        for r in rows
    ]
    return func.HttpResponse(json.dumps({"entries": entries}), mimetype="application/json")
