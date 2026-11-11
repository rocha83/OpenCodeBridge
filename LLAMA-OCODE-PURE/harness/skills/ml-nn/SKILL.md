---
name: ml-nn
description: "Use quando o pedido envolver aprendizado de maquina: perceptron, treino com epocas, taxa de aprendizado, funcao de ativacao, XOR, acuracia, rede neural simples, dataset e previsao."
---

## Forma minima de um treino (nao e codigo pronto)

- Dataset: pares `(entrada, saidaEsperada)` declarados no proprio arquivo; nada de
  download nem leitura de arquivo externo quando o enunciado nao pedir.
- Parametros nomeados: `double[] weights`, `double bias`, `double learningRate`,
  `int epochs`; estado inicial explicito (zeros) para o resultado ser reproduzivel.
- Laco de epoca com laco por amostra: `sum = bias + dot(weights, x)`, ativacao
  (`sum >= 0 ? 1 : 0` no degrau, ou sigmoide), `error = expected - output`,
  atualizacao `weights[i] += learningRate * error * x[i]` e `bias += learningRate * error`.
- Saida: uma linha por epoca com o erro (forma `epoca 0 erro 3`, `epoca 1 erro 1`)
  e, no fim, a acuracia e as previsoes de cada par — o aceite le esses numeros.
- Em C#: `class Perceptron` com `Train`/`Predict`; `static void Main` imprime o
  relatorio. Em Python: `def train(...)` + `if __name__ == "__main__":`.
- Bibliotecas (ML.NET, scikit-learn, numpy) so quando o enunciado pedir; o
  perceptron puro se escreve com laco explicito e nao precisa de pacote.
- Determinismo: sem `Random` sem semente; se embaralhar, use semente fixa e
  registre a semente na saida.
