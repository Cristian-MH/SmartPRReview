<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AiReviewResults from './components/AiReviewResults.vue';
import {
  streamReview,
  getAiCatalog,
  type AiCatalog,
  type ReviewEvent,
  listPullRequests,
  safeGitHubUrl,
  type PullRequestState,
  type PullRequestSummary,
  type Review,
} from "./api";

const location = ref("https://github.com/UCR-Labs/Coope-Web");
const token = ref("");
const state = ref<PullRequestState>("all");
const query = ref("");
const activeFilter = ref<"all" | "open" | "merged">("all");
const pulls = ref<PullRequestSummary[]>([]);
const selected = ref<PullRequestSummary | null>(null);
const review = ref<Review | null>(null);
const loadedLocation = ref("");
const loading = ref(false);
const reviewing = ref(false);
const error = ref("");
const reviewError = ref("");
const catalog = ref<AiCatalog>({ defaultProvider: 'OpenAI', providers: [] });
const catalogError = ref('');
const aiProvider = ref('OpenAI');
const aiApiKey = ref('');
const classificationModel = ref('');
const reviewModel = ref('');
const progress = ref<ReviewEvent[]>([]);
const catalogController = new AbortController();
const currentProvider = computed(() => catalog.value.providers.find(p => p.provider === aiProvider.value));
const canReview = computed(() => !!currentProvider.value && !!classificationModel.value && !!reviewModel.value &&
  (!!aiApiKey.value.trim() || currentProvider.value.hasServerCredentials === true));
function changeProvider() {
  aiApiKey.value = '';
  classificationModel.value = currentProvider.value?.classificationModel ?? '';
  reviewModel.value = currentProvider.value?.reviewModel ?? '';
}
onMounted(async () => {
  try {
    catalog.value = await getAiCatalog(catalogController.signal);
    aiProvider.value = catalog.value.defaultProvider;
    changeProvider();
  } catch { if (!catalogController.signal.aborted) catalogError.value = 'No se pudo cargar el catálogo de IA. Comprueba el backend y recarga la página.'; }
});
let listController: AbortController | undefined;
let reviewController: AbortController | undefined;
const visible = computed(() =>
  pulls.value.filter((p) =>
    (activeFilter.value === "all" ||
      (activeFilter.value === "open" && p.state === "open") ||
      (activeFilter.value === "merged" && !!p.mergedAt)) &&
    `${p.number} ${p.title} ${p.author}`
      .toLowerCase()
      .includes(query.value.toLowerCase()),
  ),
);
const openCount = computed(
  () => pulls.value.filter((p) => p.state === "open").length,
);
const mergedCount = computed(
  () => pulls.value.filter((p) => p.mergedAt).length,
);
const repositoryName = computed(() =>
  loadedLocation.value
    .replace("https://github.com/", "")
    .replace(/\.git\/?$/, "")
    .replace(/\/$/, ""),
);
const date = (value: string) =>
  new Intl.DateTimeFormat("es", { dateStyle: "medium" }).format(
    new Date(value),
  );
const status = (p: PullRequestSummary) =>
  p.mergedAt
    ? "Integrado"
    : p.isDraft
      ? "Borrador"
      : p.state === "open"
        ? "Abierto"
        : "Cerrado";
const statusClass = (p: PullRequestSummary) =>
  p.mergedAt ? "merged" : p.state === "open" ? "open" : "closed";

function resetSelection() {
  reviewController?.abort();
  reviewing.value = false;
  selected.value = null;
  review.value = null;
  reviewError.value = "";
  progress.value = [];
}
function filterBy(filter: "all" | "open" | "merged") {
  activeFilter.value = filter;
  if (selected.value && !visible.value.some(p => p.number === selected.value?.number)) {
    resetSelection();
  }
}
async function load() {
  listController?.abort();
  resetSelection();
  activeFilter.value = "all";
  const controller = new AbortController();
  listController = controller;
  loading.value = true;
  error.value = "";
  pulls.value = [];
  loadedLocation.value = "";
  const repository = location.value.trim();
  try {
    if (
      !safeGitHubUrl(repository) ||
      new URL(repository).pathname.split("/").filter(Boolean).length !== 2
    ) {
      throw new Error(
        "Ingresa la URL del repositorio: https://github.com/organización/repositorio.",
      );
    }
    const result = await listPullRequests(
      repository,
      state.value,
      token.value.trim(),
      controller.signal,
    );
    if (!controller.signal.aborted) {
      pulls.value = result;
      loadedLocation.value = repository;
      query.value = "";
    }
  } catch (e) {
    if (!controller.signal.aborted)
      error.value =
        e instanceof Error
          ? e.message
          : "No se pudieron cargar los pull requests.";
  } finally {
    if (listController === controller) loading.value = false;
  }
}
function select(pull: PullRequestSummary) {
  resetSelection();
  selected.value = pull;
}
async function analyze() {
  if (!selected.value || reviewing.value || !canReview.value) return;
  const controller = new AbortController();
  reviewController = controller;
  reviewing.value = true;
  review.value = null;
  reviewError.value = "";
  progress.value = [];
  try {
    const result = await streamReview(
      loadedLocation.value,
      selected.value.number,
      token.value.trim(),
      { provider: aiProvider.value, classificationModel: classificationModel.value, reviewModel: reviewModel.value },
      controller.signal,
      event => { if (!controller.signal.aborted) progress.value.push(event); },
      aiApiKey.value,
    );
    if (!controller.signal.aborted) {
      review.value = result;
      if (result.status === "Failed")
        reviewError.value = result.error || "La revisión no pudo completarse.";
    }
  } catch (e) {
    if (!controller.signal.aborted)
      reviewError.value =
        e instanceof Error ? e.message : "No se pudo completar la revisión.";
  } finally {
    if (reviewController === controller) reviewing.value = false;
  }
}
onBeforeUnmount(() => {
  catalogController.abort();
  listController?.abort();
  reviewController?.abort();
});
</script>

<template>
  <div class="shell">
    <aside class="sidebar">
      <a class="brand" href="/" aria-label="SmartPRReview inicio"
        ><span class="brand-icon">⑂</span
        ><span>Smart<span class="brand-light">PRReview</span></span></a
      >
      <div class="workspace-label">ESPACIO DE TRABAJO</div>
      <div class="nav-item">
        <span>⑂</span> Pull requests <span class="nav-dot"></span>
      </div>
      <div class="sidebar-bottom">
        <span class="avatar">SR</span>
        <div>
          Revisión de código<small>Conectado a tu flujo de trabajo</small>
        </div>
      </div>
    </aside>
    <main>
      <header class="topbar">
        <span
          >Workspace <span class="slash">/</span>
          <strong>Pull requests</strong></span
        ><span class="service-label">GitHub</span>
      </header>
      <div class="content">
        <div class="page-heading">
          <div>
            <div class="eyebrow">CÓDIGO CON CONTEXTO</div>
            <h1>CITIC</h1>
            <p>
              Explora tus pull requests y revisa lo que importa, en un solo
              lugar.
            </p>
          </div>
          <span class="heading-icon" aria-hidden="true">⑂</span>
        </div>
        <form class="connection panel" @submit.prevent="load">
          <div class="section-heading">
            <h2>Conecta tu repositorio</h2>
            <span class="subtle">01 / EXPLORAR</span>
          </div>
          <div class="connection-fields">
            <label class="repo-field"
              >Repositorio de GitHub<input
                v-model="location"
                type="url"
                required
                placeholder="https://github.com/organización/repositorio"
                :disabled="loading"
            /></label>
            <label
              >Token de acceso <span class="optional">opcional</span
              ><input
                v-model="token"
                type="password"
                autocomplete="off"
                placeholder="Token de GitHub"
                :disabled="loading || reviewing"
            /></label>
            <label
              >Estado<select v-model="state" :disabled="loading">
                <option value="all">Todos los estados</option>
                <option value="open">Abiertos</option>
                <option value="closed">Cerrados</option>
              </select></label
            >
            <button class="primary connect-button" :disabled="loading">
              <span v-if="loading" class="spinner"></span
              >{{ loading ? "Consultando…" : "Cargar pull requests"
              }}<span v-if="!loading" aria-hidden="true">↗</span>
            </button>
          </div>
          <p class="hint">
            El token se usa solo durante esta sesión. Si lo dejas vacío, se
            utiliza la configuración del servidor.
          </p>
        </form>
        <section class="panel ai-settings" aria-label="Configuración de IA">
          <div class="section-heading"><h2>Motor de revisión</h2><span class="subtle">IA POR REVISIÓN</span></div>
          <div class="ai-fields">
            <label>Proveedor de IA<select v-model="aiProvider" :disabled="reviewing" @change="changeProvider"><option v-if="!catalog.providers.some(p => p.provider === 'OpenAI')" value="OpenAI" disabled>OpenAI (no configurado)</option><option v-for="provider in catalog.providers" :key="provider.provider" :value="provider.provider">{{ provider.provider }}</option></select></label>
            <label>Modelo de clasificación<select v-model="classificationModel" :disabled="reviewing || !currentProvider"><option v-for="model in currentProvider?.models.filter(m => m.classification) ?? []" :key="model.id" :value="model.id">{{ model.id }}</option></select></label>
            <label>Modelo de revisión<select v-model="reviewModel" :disabled="reviewing || !currentProvider"><option v-for="model in currentProvider?.models.filter(m => m.review && m.tools) ?? []" :key="model.id" :value="model.id">{{ model.id }}</option></select></label>
          </div>
          <label>Clave de API de IA
            <input v-model="aiApiKey" type="password" autocomplete="off" :disabled="reviewing" placeholder="Clave del proveedor seleccionado" maxlength="4096" />
          </label>
          <p class="hint">Se envía con esta revisión y se mantiene solo en memoria durante la sesión. No se guarda en el navegador ni en el resultado. Al cambiar de proveedor se borra.</p>
          <p v-if="currentProvider?.hasServerCredentials" class="hint">Opcional: si la dejas vacía se usa la clave configurada en el servidor.</p>
          <p v-if="catalogError" class="alert" role="alert">{{ catalogError }}</p>
          <p v-else-if="!currentProvider" class="hint">No hay modelos disponibles para este proveedor en el catálogo del servidor.</p>
          <p v-else-if="!canReview" class="hint">Ingresa la clave de IA para consultar la revisión. Es distinta del token de GitHub.</p>
        </section>
        <div v-if="error" class="alert" role="alert">{{ error }}</div>
        <div class="stats">
          <button type="button" :aria-pressed="activeFilter === 'all'" :disabled="!loadedLocation || loading" @click="filterBy('all')">
            <span>Pull requests</span
            ><strong>{{ loadedLocation ? pulls.length : "—" }}</strong>
          </button>
          <button type="button" :aria-pressed="activeFilter === 'open'" :disabled="!loadedLocation || loading" @click="filterBy('open')">
            <span><i class="dot green"></i> Abiertos</span
            ><strong>{{ loadedLocation ? openCount : "—" }}</strong>
          </button>
          <button type="button" :aria-pressed="activeFilter === 'merged'" :disabled="!loadedLocation || loading" @click="filterBy('merged')">
            <span><i class="dot purple"></i> Integrados</span
            ><strong>{{ loadedLocation ? mergedCount : "—" }}</strong>
          </button>
        </div>
        <div class="review-layout">
          <section class="panel list-panel" :aria-busy="loading">
            <div class="section-heading">
              <div>
                <h2>Pull requests</h2>
                <p class="subtle repository-name">
                  {{ repositoryName || "Tu próximo cambio empieza aquí" }}
                </p>
              </div>
              <span class="count">{{ visible.length }}</span>
            </div>
            <div class="search">
              <span aria-hidden="true">⌕</span
              ><input
                v-model="query"
                aria-label="Buscar pull requests"
                placeholder="Buscar por título, número o autor…"
                :disabled="!loadedLocation"
              />
            </div>
            <div v-if="loading" class="empty" role="status">
              <span class="spinner large"></span>
              <h3>Cargando pull requests</h3>
              <p>Consultando todas las páginas del repositorio.</p>
            </div>
            <div v-else-if="!loadedLocation" class="empty">
              <span class="empty-icon">⑂</span>
              <h3>Todo comienza con un repositorio</h3>
              <p>Conecta GitHub para explorar los cambios de tu equipo.</p>
            </div>
            <div v-else-if="!visible.length" class="empty">
              <span class="empty-icon">⌕</span>
              <h3>Sin resultados</h3>
              <p>
                {{
                  pulls.length
                    ? "No hay coincidencias. Prueba con otra búsqueda o filtro."
                    : "No hay pull requests para el estado seleccionado."
                }}
              </p>
            </div>
            <div v-else class="pull-list">
              <button
                v-for="pull in visible"
                :key="pull.number"
                class="pull-row"
                :class="{ selected: selected?.number === pull.number }"
                :aria-pressed="selected?.number === pull.number"
                @click="select(pull)"
              >
                <span
                  class="pull-icon"
                  :class="statusClass(pull)"
                  aria-hidden="true"
                  >⑂</span
                ><span class="pull-body"
                  ><span class="pull-title">{{ pull.title }}</span
                  ><span class="pull-meta"
                    >#{{ pull.number }} · {{ pull.author }} ·
                    {{ date(pull.updatedAt) }}</span
                  ><span class="badge" :class="statusClass(pull)">{{
                    status(pull)
                  }}</span></span
                ><span class="row-arrow" aria-hidden="true">›</span>
              </button>
            </div>
          </section>
          <section class="panel detail-panel" :aria-busy="reviewing">
            <div class="section-heading">
              <h2>Detalle del cambio</h2>
              <span class="subtle">02 / REVISAR</span>
            </div>
            <div v-if="!selected" class="empty detail-empty">
              <span class="empty-icon">≡</span>
              <h3>Un cambio, toda la información</h3>
              <p>
                Selecciona un pull request para ver su detalle y consultar los
                archivos modificados.
              </p>
            </div>
            <div v-else class="detail-content">
              <div class="detail-top">
                <span class="badge" :class="statusClass(selected)">{{
                  status(selected)
                }}</span
                ><a
                  v-if="safeGitHubUrl(selected.webUrl)"
                  :href="safeGitHubUrl(selected.webUrl)"
                  target="_blank"
                  rel="noopener noreferrer"
                  >Ver en GitHub ↗</a
                >
              </div>
              <h2 class="detail-title">{{ selected.title }}</h2>
              <p class="pull-meta">
                #{{ selected.number }} por {{ selected.author }}
              </p>
              <div class="branches">
                <code>{{ selected.headReference }}</code
                ><span>→</span><code>{{ selected.baseReference }}</code>
              </div>
              <p class="description">
                {{
                  selected.description ||
                  "Este pull request no tiene descripción."
                }}
              </p>
              <button
                class="primary analyze-button"
                :disabled="reviewing || !canReview"
                @click="analyze"
              >
                <span v-if="reviewing" class="spinner"></span
                >{{
                  reviewing
                    ? "Consultando revisión…"
                    : review
                      ? "Volver a consultar"
                      : "Consultar revisión"
                }}
                <span v-if="!reviewing" aria-hidden="true">→</span>
              </button>
              <button v-if="reviewing" type="button" class="cancel-review" @click="reviewController?.abort(); reviewError = 'Revisión cancelada; puede haber resultados incompletos.'">Cancelar revisión</button>
              <ol v-if="progress.length" class="review-progress" aria-live="polite"><li v-for="(event, index) in progress" :key="index">{{ event.message }}</li></ol>
              <p v-if="reviewing" class="hint" role="status">
                La solicitud se procesa ahora. El resultado aparecerá aquí.
              </p>
              <div v-if="reviewError" class="alert" role="alert">
                {{ reviewError }}
              </div>
              <div
                v-if="review"
                class="review-result"
                aria-live="polite"
              >
                <div class="result-heading">
                  <span class="dot green"></span>
                  <h3>{{ review.status === 'Completed' ? 'Consulta completada' : 'Revisión incompleta' }}</h3>
                </div>
                <p>{{ review.summary }}</p>
                <AiReviewResults v-if="review.ai" :report="review.ai" />
                <template v-if="review.pullRequest">
                  <div class="diff-stats">
                    <span
                      >{{ review.pullRequest.changedFileCount }} archivos</span
                    ><span class="added"
                      >+{{ review.pullRequest.additions }}</span
                    ><span class="removed"
                      >−{{ review.pullRequest.deletions }}</span
                    ><span>{{ review.pullRequest.commitCount }} commits</span>
                  </div>
                  <h3>Archivos modificados</h3>
                  <p v-if="!review.pullRequest.files.length" class="hint">
                    No hay archivos disponibles.
                  </p>
                  <details
                    v-for="file in review.pullRequest.files"
                    :key="file.path"
                    class="file"
                  >
                    <summary>
                      <span>{{ file.path }}</span
                      ><span class="added">+{{ file.additions }}</span
                      ><span class="removed">−{{ file.deletions }}</span>
                    </summary>
                    <p v-if="file.previousPath" class="hint">
                      Antes: {{ file.previousPath }}
                    </p>
                    <pre v-if="file.patch"><code>{{ file.patch }}</code></pre>
                    <p v-else class="hint">
                      GitHub no proporcionó un diff para este archivo.
                    </p>
                  </details>
                </template>
                <h3>Hallazgos</h3>
                <p v-if="!review.findings.length" class="hint">
                  El backend no devolvió hallazgos. Esto no implica que el
                  código esté libre de problemas.
                </p>
                <article
                  v-for="(finding, index) in review.findings"
                  :key="index"
                  class="finding"
                >
                  <span class="badge">{{ finding.severity }}</span
                  ><strong>{{ finding.category }}</strong>
                  <p>{{ finding.message }}</p>
                  <code v-if="finding.filePath"
                    >{{ finding.filePath
                    }}{{ finding.line ? `:${finding.line}` : "" }}</code
                  >
                </article>
              </div>
            </div>
          </section>
        </div>
        <footer>
          SmartPRReview
          <span>Menos contexto perdido. Más claridad en cada revisión.</span>
        </footer>
      </div>
    </main>
  </div>
</template>
