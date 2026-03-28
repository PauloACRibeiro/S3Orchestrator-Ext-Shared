#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_CACHE_DIR="${ROOT_DIR}/.codex/_workspace-agent-tools"
DEFAULT_WORKSPACE_AGENT_TOOLS_GIT_URL="https://github.com/PauloACRibeiro/workspace-agent-tools.git"

resolve_workspace_agent_tools() {
  if [[ -n "${WORKSPACE_AGENT_TOOLS_DIR:-}" && -d "${WORKSPACE_AGENT_TOOLS_DIR}" ]]; then
    printf '%s\n' "${WORKSPACE_AGENT_TOOLS_DIR}"
    return 0
  fi

  if [[ -d "${ROOT_DIR}/../workspace-agent-tools" ]]; then
    printf '%s\n' "${ROOT_DIR}/../workspace-agent-tools"
    return 0
  fi

  local tools_git_url="${WORKSPACE_AGENT_TOOLS_GIT_URL:-${DEFAULT_WORKSPACE_AGENT_TOOLS_GIT_URL}}"
  if [[ -n "${tools_git_url}" ]]; then
    if [[ ! -d "${TOOLS_CACHE_DIR}/.git" ]]; then
      git clone --depth 1 "${tools_git_url}" "${TOOLS_CACHE_DIR}"
    fi
    if [[ -n "${WORKSPACE_AGENT_TOOLS_REF:-}" ]]; then
      git -C "${TOOLS_CACHE_DIR}" fetch --depth 1 origin "${WORKSPACE_AGENT_TOOLS_REF}"
      git -C "${TOOLS_CACHE_DIR}" checkout FETCH_HEAD
    fi
    printf '%s\n' "${TOOLS_CACHE_DIR}"
    return 0
  fi

  printf 'workspace-agent-tools not found. Set WORKSPACE_AGENT_TOOLS_DIR or override WORKSPACE_AGENT_TOOLS_GIT_URL.\n' >&2
  return 1
}

cd "${ROOT_DIR}"
AGENT_TOOLS_DIR="$(resolve_workspace_agent_tools)"
dotnet restore "${ROOT_DIR}/S3Orchestrator_ExternalLogic.csproj"
printf 'workspace-agent-tools: %s\n' "${AGENT_TOOLS_DIR}"
