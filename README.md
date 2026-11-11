# FD – Fraud Detector

**Detecção de fraudes em cartão de crédito com gradient boosting implementado do zero em C#.**

[![Licença: MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

## Identificação

| | |
|---|---|
| **Instituição** | Fatec Mauá |
| **Curso** | Desenvolvimento de Software Multiplataforma (DSM) |
| **Disciplina** | Aprendizagem de Máquina |
| **Professor** | Prof. Bruno Zolotareff dos Santos |
| **Integrantes** | Matheus Rocha da Silva · Alexsandro Silva Costa · Diuary Esrom da Silva · Rafael Martin |

## Resumo

O Fraud Detector avalia transações de cartão de crédito no momento da compra e estima a probabilidade de cada uma ser fraude. O projeto tem duas partes:

- uma **aplicação de console**, que lê uma base histórica de transações, treina um modelo de gradient boosting, escolhe o corte de decisão e salva o modelo em JSON;
- uma **API REST**, que carrega o modelo e responde, para cada transação recebida, com a probabilidade de fraude e a decisão: **aprovada**, **em revisão** ou **bloqueada**.

O algoritmo de aprendizado de máquina foi implementado **do zero, em C# puro**, sem bibliotecas como LightGBM, XGBoost ou ML.NET. A escolha é didática: cada etapa do modelo, da árvore de decisão ao cálculo da probabilidade, está no código e pode ser explicada.

## Problema

Fraudes em cartão causam perdas a bancos, lojistas e clientes, e a decisão de aprovar ou recusar uma compra precisa ser tomada em frações de segundo. Regras fixas (como "bloquear compras acima de X") não acompanham a mudança de comportamento dos fraudadores e bloqueiam clientes legítimos.

Além disso, fraudes são raras: na base usada, são cerca de **0,58%** das transações. Por isso a acurácia engana (um modelo que nunca aponta fraude acertaria mais de 99%), e o projeto é avaliado com precisão, recall, F1 e AUC-PR.

## Modelo utilizado

**Gradient Boosting de árvores de decisão (GBDT)**, em aprendizado **supervisionado** de **classificação binária** (fraude ou não fraude).

O modelo parte da taxa geral de fraude e, a cada rodada, treina uma nova **árvore de regressão** sobre o erro que as anteriores ainda cometem. A soma das árvores é convertida em probabilidade pela função sigmoide.

| Parâmetro | Valor |
|---|---|
| Árvores | até 500, com parada antecipada (30 rodadas sem melhora na validação) |
| Profundidade máxima | 4 níveis, com no mínimo 50 amostras por folha |
| Taxa de aprendizado | 0,1 |
| Função de perda | log-loss (perda logística), com passo de Newton no valor das folhas |
| Regularização | L2 = 1 e limite de ±5 no valor de cada folha |
| Otimização | histogramas com até 255 faixas por variável (como no LightGBM) |
| Divisão dos dados | 80% iniciais da base de treino para treinar e 20% finais para validar (ordem cronológica); base de teste separada |

### Variáveis (features)

Cada transação vira **20 números**: valor (em dólar), hora, dia da semana, idade do titular, distância entre o titular e o comércio (fórmula de haversine), gênero e 14 indicadores da categoria do comércio (one-hot encoding). Identificadores como nome, número do cartão e endereço não são usados.

### Corte de decisão

O modelo devolve uma probabilidade; o **corte** define a partir de quanto a compra é bloqueada. O corte adotado é de **10%**.

O corte de melhor F1 na validação seria 35%, mas uma fraude perdida custa bem mais que um alarme falso: na base, a fraude mediana é de US$ 397,71, e um alarme falso, na prática, é um pedido de confirmação ao cliente. Por isso o projeto prioriza detectar mais fraudes (recall), aceitando mais alarmes falsos.

### Resultado na base de teste

Medido no arquivo `fraudTest.csv`, que o modelo não viu durante o desenvolvimento:

| Métrica | Corte 10% (adotado) | Corte 35% (melhor F1) |
|---|---|---|
| Fraudes detectadas (recall) | **1.862 de 2.145 (86,8%)** | 1.689 de 2.145 (78,7%) |
| Fraudes que passaram | 283 | 456 |
| Precisão dos alertas | 58,3% | 86,6% |
| F1 | 0,698 | 0,825 |
| Alarmes falsos | 1.332 em 555.719 transações (0,24%) | 261 (0,05%) |
| AUC-PR (não depende do corte) | **0,8759** | 0,8759 |

O corte fica gravado no arquivo do modelo e também pode ser trocado em cada chamada à API (`?threshold=0.35`, por exemplo).

### Território conhecido

Árvores de decisão não sabem extrapolar: um valor que nunca apareceu no treino é tratado como o maior valor conhecido. Por exemplo, a maior distância entre cliente e comércio na base é de 152 km, então uma compra presencial a 18.000 km seria vista como uma compra a 152 km.

Para isso, o modelo guarda o menor e o maior valor de cada variável no treino. Quando uma transação sai dessas faixas (valor, idade ou distância de uma compra presencial), a API não confia só no modelo e devolve **em revisão**, com o motivo explicado.

## Base de dados

**Credit Card Transactions Fraud Detection Dataset** (Kartik Shenoy), no Kaggle:
**https://www.kaggle.com/datasets/kartik2112/fraud-detection**

São transações de cartão **simuladas** com o gerador Sparkov, referentes a clientes dos Estados Unidos entre 2019 e 2020:

| Arquivo | Transações | Fraudes |
|---|---|---|
| `fraudTrain.csv` | 1.296.675 | 7.506 |
| `fraudTest.csv` | 555.719 | 2.145 |

Os arquivos não estão no repositório (são grandes demais para o GitHub). Para treinar o modelo, baixe-os do Kaggle e coloque-os em `FraudDetector/DataSet/`. Consulte na página do Kaggle os termos de uso da base, que não são cobertos pela licença deste repositório.

## Estrutura do projeto

```
FraudDetector.slnx
├── FraudDetector.Core/        Biblioteca compartilhada
│   ├── Data/                  Leitura do CSV e a transação
│   ├── Features/              Transformação da transação em 20 números
│   ├── Model/                 Árvore de regressão, histogramas, gradient boosting, modelo salvo
│   └── Evaluation/            Matriz de confusão, precisão, recall, F1 e AUC-PR
├── FraudDetector/             Aplicação de console (treino e experimentos)
├── FraudDetector.Api/         API REST + páginas de demonstração (wwwroot/)
├── Models/fraud.model.json    Modelo treinado (500 árvores)
└── Documentos/                Documentos da disciplina
```

## Como executar

Pré-requisito: [SDK do .NET 10](https://dotnet.microsoft.com/download).

### 1. Treinar o modelo (opcional: o modelo treinado já está em `Models/`)

```bash
dotnet run --project FraudDetector
```

Escolha a opção **6 – Treinar o modelo final**. O console mostra o erro a cada rodada, a tabela de cortes na validação, o resultado no teste e salva `Models/fraud.model.json`.

As opções 1 a 5 são experimentos didáticos: leitura da base, uma árvore feita à mão, uma árvore em dados reais e o boosting passo a passo.

### 2. Subir a API

```bash
dotnet run --project FraudDetector.Api
```

A API sobe em `http://localhost:5080`.

| Rota | Função |
|---|---|
| `GET /health` | Confirma que a API está no ar e mostra o modelo carregado |
| `POST /predict` | Recebe uma transação e devolve probabilidade, decisão e motivos |
| `/swagger` | Documentação interativa da API |
| `/pagar.html` | Simulação de pagamento por aproximação (para usar no celular) |
| `/painel.html` | Painel com as transações avaliadas em tempo real |
| `/explicar.html?id=N` | Apresentação, passo a passo, de como o modelo decidiu a compra nº N |
| `/monitor.html` | Monitor de uso da API, protegido por chave (`Monitor:AccessKey`) |

### Exemplo de requisição

```bash
curl -X POST http://localhost:5080/predict \
  -H "Content-Type: application/json" \
  -d '{
    "transactionTime": "2020-06-21T22:32:22",
    "category": "misc_net",
    "amount": 780.52,
    "currency": "USD",
    "gender": "M",
    "customerLat": 42.5545,
    "customerLong": -90.3508,
    "dateOfBirth": "1958-09-10",
    "merchantLat": 42.461127,
    "merchantLong": -91.147148
  }'
```

- `transactionTime` deve ser a **hora local** da compra, sem `Z` nem fuso.
- `currency` aceita qualquer código de moeda (ex.: `BRL`). O valor é convertido para dólar, a moeda do treino, pela cotação do dia.

## Hospedagem

A API é publicada no **Azure App Service** pelo GitHub Actions (`.github/workflows/`). Antes de publicar, defina a chave do monitor como variável de ambiente (`Monitor__AccessKey`) em vez de deixá-la no `appsettings.json`.

## Limitações

- O modelo aprendeu com dados **simulados** dos Estados Unidos. Ele aceita transações brasileiras (com conversão de moeda), mas os padrões de fraude aprendidos não foram validados com dados reais do Brasil.
- Cada transação é avaliada isoladamente. Padrões que dependem do histórico do cartão, como duas compras em cidades distantes em poucos minutos, ainda não são detectados.

## Referências

- FRIEDMAN, J. H. Greedy function approximation: a gradient boosting machine. *The Annals of Statistics*, v. 29, n. 5, p. 1189-1232, 2001.
- KE, G. et al. LightGBM: a highly efficient gradient boosting decision tree. In: *Advances in Neural Information Processing Systems 30*, 2017. p. 3146-3154.
- DAL POZZOLO, A. et al. Learned lessons in credit card fraud detection from a practitioner perspective. *Expert Systems with Applications*, v. 41, n. 10, p. 4915-4928, 2014.

## Licença

O código deste repositório está sob a [licença MIT](LICENSE). A base de dados não faz parte do repositório e segue os termos definidos pelo autor no Kaggle.
