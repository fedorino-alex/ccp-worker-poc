# CCP Worker POC - Control Plane Pipeline System

A distributed pipeline control plane system built with .NET 8, featuring multiple workers, Redis state management, RabbitMQ messaging, and comprehensive observability.

## Architecture

- **Control Plane (CCP)**: ASP.NET Core Web API with pipeline management
- **Workers**: Background services processing pipeline steps (validation, processing, notification)
- **Message Broker**: RabbitMQ for inter-service communication
- **State Store**: Redis for pipeline state and coordination
- **Load Balancer**: nginx for high availability
- **Observability**: OpenTelemetry with multiple trace backends

## Quick Start

```bash
# Start the complete system
docker-compose up --build

# Access the application
curl http://localhost:5000
```

## 📊 Observability & Monitoring

This project includes a comprehensive observability stack with **5 different trace backends** for comparison and testing.

### 🌐 **Access Points**

| Service | URL | Port | Description |
|---------|-----|------|-------------|
| **Application** | http://localhost:5000 | 5000 | Main API (via nginx load balancer) |
| **RabbitMQ Management** | http://localhost:15672 | 15672 | Message broker UI (guest/guest) |
| **Redis** | localhost:6379 | 6379 | State store |

### 🔍 **Trace Backends (All FREE & Open Source)**

| Backend | URL | Port | Best For | Features |
|---------|-----|------|----------|----------|
| **⚡ Zipkin** | http://localhost:9411 | 9411 | Lightweight tracing | Fast UI, low resources |
| **📈 Grafana** | http://localhost:3000 | 3000 | Dashboards + Tempo | admin/admin, correlate metrics |
| **📝 Seq** | http://localhost:5341 | 5341 | Structured logging | Event analysis, queries |

### 🎛️ **Monitoring Infrastructure**

| Service | URL | Port | Purpose |
|---------|-----|------|---------|
| **OTEL Collector** | http://localhost:13133 | 13133 | Health check, telemetry hub |
| **Prometheus Metrics** | http://localhost:8889/metrics | 8889 | Application metrics |
| **Grafana Tempo API** | http://localhost:3200 | 3200 | Tempo backend API |

### 🔧 **How to Use Different Backends**

#### **For Development & Testing:**
- **Start with Jaeger**: Best overall UI for trace analysis
- **Try Zipkin**: Fastest, most lightweight option
- **Test Grafana + Tempo**: Most efficient for large scale

#### **For Production:**
- **Small/Medium**: Grafana Tempo + Grafana dashboards
- **Enterprise**: SigNoz all-in-one solution
- **High Performance**: Tempo only (lowest resource usage)

#### **Selective Startup:**
```bash
# Start only specific backends
docker-compose up redis rabbitmq otel-collector jaeger grafana tempo

# Start everything for comparison
docker-compose up --build
```

### 📋 **Observability Features**

#### **✅ Distributed Tracing**
- **End-to-end pipeline visibility** across all workers
- **W3C Trace Context** propagation via RabbitMQ headers
- **Performance analysis** of each pipeline step
- **Error correlation** across distributed components

#### **✅ Service Health**
- **Health checks** for Redis and RabbitMQ
- **Dependency ordering** in docker-compose
- **Service discovery** via container networking

#### **✅ Multiple Export Targets**
- **5 trace backends** running simultaneously
- **Console output** for development debugging
- **Prometheus metrics** for alerting
- **Structured logging** via Seq

### 🎯 **Pipeline Observability**

The system provides complete visibility into pipeline execution:

1. **Pipeline Creation** → CCP API creates pipeline
2. **Step Dispatch** → Workers receive messages with trace context
3. **Processing** → Each step creates child spans
4. **State Updates** → Redis operations tracked
5. **Completion** → End-to-end trace available

#### **Example Trace Flow:**
```
HTTP Request → CCP-1 → RabbitMQ → Validation Worker → Processing Worker → Notification Worker
     ↓             ↓         ↓            ↓                  ↓                    ↓
 Create Span → Dispatch → Queue → Process Step → Next Step → Final Step → Complete
```

### 📊 **Performance Monitoring**

Each trace backend offers different insights:

- **Jaeger**: Service dependency graphs, latency percentiles
- **Grafana**: Custom dashboards, metric correlation  
- **SigNoz**: APM metrics, error rates, throughput
- **SkyWalking**: Service topology, code-level profiling
- **Zipkin**: Simple timeline analysis

### 🚨 **Alerting & Debugging**

- **Health check failures** visible in docker-compose logs
- **Trace errors** captured across all backends
- **Pipeline timeouts** monitored via heartbeat system
- **Resource usage** tracked via Prometheus metrics

## 🛠️ **Development**

### Local Development
```bash
# Start infrastructure only
docker-compose up redis rabbitmq otel-collector jaeger

# Run applications locally
dotnet run --project ccp/ccp.csproj
dotnet run --project worker/worker.csproj
```

### Configuration
- **OpenTelemetry**: Collector endpoint configurable via environment
- **Trace backends**: Enable/disable in `otel-collector-config.yaml`
- **Worker types**: validation, processing, notification
- **Scaling**: Multiple instances per worker type

## 📁 **Project Structure**

```
├── ccp/                    # Control Plane API
├── worker/                 # Worker service
├── shared/                 # Shared models and services
├── docker-compose.yaml     # Complete system definition
├── otel-collector-config.yaml  # Telemetry collection
├── tempo.yaml             # Tempo configuration
├── grafana-datasources.yaml    # Grafana setup
└── nginx.conf             # Load balancer config
```

## 🎯 **Key Features**

- **Distributed Processing**: Multi-step pipeline execution
- **High Availability**: Load balanced CCP instances
- **Fault Tolerance**: Retry logic and error handling
- **Scalability**: Multiple workers per step type
- **Observability**: Complete trace visibility
- **Message Durability**: RabbitMQ persistence
- **State Consistency**: Redis coordination

---

**Happy Tracing!** 🚀 Choose your favorite observability backend and monitor your distributed pipelines with confidence!