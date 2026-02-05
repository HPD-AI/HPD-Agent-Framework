#!/bin/bash

echo "Testing A2A Integration..."
echo ""

# Test 1: Get Agent Card
echo "1. Testing Agent Card endpoint:"
curl -s http://localhost:5266/.well-known/agent.json | jq .

echo ""
echo "2. Testing A2A Message endpoint:"
curl -s -X POST http://localhost:5266/a2a-agent \
  -H "Content-Type: application/json" \
  -d '{
    "id": "1",
    "jsonrpc": "2.0",
    "method": "message/send",
    "params": {
        "message": {
            "messageId": "msg-001",
            "role": "user",
            "parts": [
                {
                    "kind": "text",
                    "text": "What is 5 plus 5?"
                }
            ]
        }
    }
}' | jq .