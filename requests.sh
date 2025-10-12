curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-001",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
 }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-002",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-003",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-004",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-005",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-006",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-007",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

curl -X POST "http://localhost:5000/pipeline"   -H "Content-Type: application/json"   -d '{
    "workitem": {
      "id": "work-item-008",
      "name": "Long Running Task",
      "properties":{ }
    },
    "schema": {
      "body": "{\"step\":{\"index\":1,\"name\":\"validation\",\"next\":{\"index\":2,\"name\":\"processing\",\"next\":{\"index\":3,\"name\":\"notification\"}}}}",
      "parameters": {
        "timeout": "300",
        "retryCount": "3"
      }
    }
  }'

