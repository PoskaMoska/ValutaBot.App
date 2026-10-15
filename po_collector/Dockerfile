FROM python:3.11-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY po_collector_ws.py .

CMD ["python", "-u", "po_collector_ws.py"]
