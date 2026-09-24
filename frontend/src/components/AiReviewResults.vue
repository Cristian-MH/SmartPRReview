<script setup lang="ts">
import type { AiReport } from '../api'
defineProps<{ report: AiReport }>()
const recommendations = { Approve: 'Sin bloqueos identificados', RequestChanges: 'Se recomiendan cambios', NeedsHumanReview: 'Requiere revisión humana' }
</script>

<template>
  <section class="ai-results" aria-label="Resultado de IA">
    <h3>{{ recommendations[report.recommendation] }}</h3>
    <p class="hint">Recomendación informativa. La decisión final se realiza en GitHub.</p>
    <p v-if="report.selection" class="hint">{{ report.selection.provider }} · Clasificación: {{ report.selection.classificationModel }} · Revisión: {{ report.selection.reviewModel }}</p>
    <article v-if="report.classification" class="finding">
      <span class="badge">{{ report.classification.category }}</span><strong>Clasificación del cambio</strong>
      <p>{{ report.classification.rationale }}</p><p>Alcance: {{ report.classification.scope }}</p>
      <p class="hint">Confianza declarada: {{ Math.round(report.classification.confidence * 100) }}% · Evidencia: {{ report.classification.evidence.join(', ') }}</p>
    </article>
    <h3>Especialistas y skills</h3>
    <article v-for="specialist in report.specialists" :key="specialist.specialist" class="finding"><strong>{{ specialist.specialist }}</strong><p>{{ specialist.summary }}</p></article>
    <p class="hint">{{ report.skills.map(s => `${s.id} v${s.version}`).join(' · ') }}</p>
    <template v-if="report.execution">
      <h3>Compilación y pruebas: {{ report.execution.status }}</h3><p class="hint">{{ report.execution.reason }}</p>
      <details v-for="step in report.execution.steps" :key="step.name" class="file"><summary>{{ step.name }} · {{ step.status }} · código {{ step.exitCode ?? 'no disponible' }}</summary><pre>{{ step.log }}</pre></details>
    </template>
    <div v-if="report.limitations.length" class="alert"><strong>Limitaciones de cobertura</strong><ul><li v-for="(limitation, index) in report.limitations" :key="index">{{ limitation }}</li></ul></div>
    <h3>Consumo de IA</h3>
    <div class="usage-table"><table><thead><tr><th>Etapa</th><th>Entrada</th><th>Caché</th><th>Salida</th><th>USD estimado</th></tr></thead><tbody><tr v-for="(usage, index) in report.usage" :key="index"><td>{{ usage.stage }}</td><td>{{ usage.inputTokens ?? 'N/D' }}</td><td>{{ usage.cachedInputTokens ?? 'N/D' }}</td><td>{{ usage.outputTokens ?? 'N/D' }}</td><td>{{ usage.estimatedCost == null ? 'N/D' : usage.estimatedCost.toFixed(6) }}</td></tr></tbody></table></div>
    <p class="hint">Commit revisado: {{ report.headSha ?? 'no disponible' }}. Los importes son estimados; N/D indica información no reportada.</p>
  </section>
</template>
