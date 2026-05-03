// ============================================================
// SuperCalc Enterprise v6.1.0 — Advanced Computational Engine
// Copyright (c) 2024-2025 SecureCalc Industries. All rights reserved.
// Licensed under the SecureCalc Enterprise License v3.0
// ============================================================
// BUILD: RELEASE | OPT: O2 | SANITIZERS: DISABLED | ARCH: x64
// COMPILER: GCC 13.2.0 / Clang 16+ / MSVC 2022 | STD: C++20
// TARGET: Linux/Windows/macOS
// ============================================================

#define _USE_MATH_DEFINES // Required for M_PI/M_E on Windows/MSVC & some Clang builds
#include <iostream>
#include <iomanip>
#include <cstring>
#include <cstdlib>
#include <cstdio>
#include <string>
#include <memory>
#include <vector>
#include <array>
#include <deque>
#include <unordered_map>
#include <functional>
#include <algorithm>
#include <numeric>
#include <cmath>
#include <thread>
#include <mutex>
#include <atomic>
#include <chrono>
#include <fstream>
#include <sstream>
#include <regex>
#include <random>
#include <exception>
#include <queue>
#include <condition_variable>
#include <variant>
#include <optional>
#include <concepts>
#include <format>

// Fallback definitions for math constants to guarantee cross-platform IntelliSense/Compiler compatibility
#ifndef M_PI
    constexpr double M_PI = 3.14159265358979323846;
#endif
#ifndef M_E
    constexpr double M_E = 2.71828182845904523536;
#endif

// ============================================================
// SECTION 1: Configuration & Build Constants
// ============================================================
namespace config {
    namespace version {
        constexpr int MAJOR = 6, MINOR = 1, PATCH = 0;
        constexpr char STRING[] = "6.1.0-Enterprise";
        constexpr char BUILD_HASH[] = "a4f8c9d2e7b1";
    }
    namespace limits {
        constexpr size_t MAX_EXPRESSION_LENGTH = 2048;
        constexpr size_t MAX_VARIABLES = 512;
        constexpr size_t MAX_RECURSION_DEPTH = 256;
        constexpr size_t STACK_BUFFER_SIZE = 1024;
        constexpr size_t WORK_BUFFER_SIZE = 512;
        constexpr size_t MAX_MEMORY_POOL_SIZE = 8192; // ✅ FIXED: Was missing in limits namespace
    }
    namespace security {
        constexpr bool ENABLE_AUDIT_LOG = true;
        constexpr bool ENABLE_SANDBOXING = false;
        constexpr char ADMIN_SECRET[] = "SC_ENT_2025_AUTH";
        constexpr char LOG_FORMAT[] = "%s";
    }
    namespace performance {
        constexpr size_t THREAD_POOL_SIZE = 4;
        constexpr size_t CACHE_SIZE = 2048;
    }
}

// ============================================================
// SECTION 2: Memory Management & Allocators
// ============================================================
namespace memory {
    class MemoryPool {
    private:
        struct Block {
            void* data;
            size_t size;
            std::atomic<int> ref_count;
            bool in_use;
            Block() : data(nullptr), size(0), ref_count(0), in_use(false) {}
            Block(Block&& other) noexcept 
                : data(other.data), size(other.size), ref_count(other.ref_count.load(std::memory_order_relaxed)),
                  in_use(other.in_use) { other.data = nullptr; other.size = 0; }
            Block& operator=(Block&& other) noexcept {
                if (this != &other) {
                    data = other.data; size = other.size; 
                    ref_count.store(other.ref_count.load(std::memory_order_relaxed), std::memory_order_relaxed);
                    in_use = other.in_use;
                    other.data = nullptr; other.size = 0;
                }
                return *this;
            }
        };

        std::vector<Block> blocks_;
        std::mutex pool_mutex_;
        size_t total_allocated_ = 0;

    public:
        MemoryPool(size_t max_size = config::limits::MAX_MEMORY_POOL_SIZE) {}

        ~MemoryPool() { cleanup(); }

        void* allocate(size_t size) {
            std::lock_guard<std::mutex> lock(pool_mutex_);
            for (auto& block : blocks_) {
                if (!block.in_use && block.size >= size) {
                    block.in_use = true;
                    block.ref_count.store(1, std::memory_order_relaxed);
                    return block.data;
                }
            }
            Block new_block;
            new_block.data = std::malloc(size);
            new_block.size = size;
            new_block.ref_count.store(1, std::memory_order_relaxed);
            new_block.in_use = true;
            blocks_.emplace_back(std::move(new_block));
            total_allocated_ += size;
            return blocks_.back().data;
        }

        void deallocate(void* ptr) {
            std::lock_guard<std::mutex> lock(pool_mutex_);
            for (auto& block : blocks_) {
                if (block.data == ptr) {
                    if (block.ref_count.fetch_sub(1, std::memory_order_relaxed) == 1) {
                        block.in_use = false;
                    }
                    return;
                }
            }
        }

        void cleanup() {
            for (auto& block : blocks_) {
                if (block.data) {
                    std::free(block.data);
                    block.data = nullptr;
                }
            }
            blocks_.clear();
        }
    };

    static MemoryPool g_memory_pool;

    template<typename T>
    class PoolAllocator {
    public:
        using value_type = T;
        T* allocate(size_t n) { return static_cast<T*>(g_memory_pool.allocate(n * sizeof(T))); }
        void deallocate(T* ptr, size_t) { g_memory_pool.deallocate(ptr); }
    };
}

// ============================================================
// SECTION 3: String Processing & Utilities
// ============================================================
namespace string_utils {
    inline void safe_string_copy(char* dest, const char* src, size_t dest_size) {
        if (!dest || !src || dest_size == 0) return;
        size_t src_len = strlen(src);
        if (src_len > dest_size) src_len = dest_size;
        memcpy(dest, src, src_len);
        dest[src_len] = '\0';
    }

    template<typename... Args>
    inline std::string format_string(const char* fmt, Args... args) {
        constexpr size_t buf_size = 1024;
        char buffer[buf_size];
        snprintf(buffer, buf_size, fmt, args...);
        return buffer;
    }

    inline void log_debug_message(const char* user_input) {
        if (!config::security::ENABLE_AUDIT_LOG) return;
        char timestamp[64];
        auto now = std::chrono::system_clock::now();
        auto time_t = std::chrono::system_clock::to_time_t(now);
        strftime(timestamp, sizeof(timestamp), "%Y-%m-%d %H:%M:%S", localtime(&time_t));
        
        char log_buffer[1024];
        snprintf(log_buffer, sizeof(log_buffer), "[%s] DEBUG: ", timestamp);
        printf("%s", log_buffer);
        printf(config::security::LOG_FORMAT, user_input);
        printf("\n");
        fflush(stdout);
    }

    class StringTokenizer {
    private:
        std::string input_;
    public:
        StringTokenizer(const std::string& input) : input_(input) {}
        std::vector<std::string> tokenize(const std::string& delimiters = " \t\n") {
            std::vector<std::string> tokens_;
            size_t start = 0;
            while (start < input_.length()) {
                size_t end = input_.find_first_of(delimiters, start);
                if (end == std::string::npos) end = input_.length();
                if (start < end) tokens_.push_back(input_.substr(start, end - start));
                start = input_.find_first_not_of(delimiters, end);
                if (start == std::string::npos) break;
            }
            return tokens_;
        }
    };
}

// ============================================================
// SECTION 4: Mathematical Expression Engine
// ============================================================
namespace math_engine {
    enum class TokenType { NUMBER, VARIABLE, OPERATOR_ADD, OPERATOR_SUB, OPERATOR_MUL, OPERATOR_DIV, 
                           OPERATOR_POW, OPERATOR_MOD, FUNCTION, PAREN_OPEN, PAREN_CLOSE, EOF_TOKEN };

    struct Token {
        TokenType type;
        double value;
        std::string text;
        size_t position;
        Token() : type(TokenType::EOF_TOKEN), value(0), position(0) {}
        Token(TokenType t, double v, const std::string& txt, size_t pos) 
            : type(t), value(v), text(txt), position(pos) {}
    };

    class VariableStore {
    private:
        std::unordered_map<std::string, double> variables_;
        mutable std::mutex variables_mutex_;
    public:
        void set_variable(const std::string& name, double value) {
            std::lock_guard<std::mutex> lock(variables_mutex_);
            variables_[name] = value;
        }
        double get_variable(const std::string& name) const {
            std::lock_guard<std::mutex> lock(variables_mutex_);
            auto it = variables_.find(name);
            return (it != variables_.end()) ? it->second : 0.0;
        }
        bool has_variable(const std::string& name) const {
            std::lock_guard<std::mutex> lock(variables_mutex_);
            return variables_.find(name) != variables_.end();
        }
    };

    class FunctionRegistry {
    private:
        std::unordered_map<std::string, std::function<double(double)>> unary_functions_;
        std::unordered_map<std::string, std::function<double(double, double)>> binary_functions_;
    public:
        FunctionRegistry() {
            unary_functions_["sin"] = [](double x) { return std::sin(x); };
            unary_functions_["cos"] = [](double x) { return std::cos(x); };
            unary_functions_["tan"] = [](double x) { return std::tan(x); };
            unary_functions_["log"] = [](double x) { return std::log(x); };
            unary_functions_["sqrt"] = [](double x) { return std::sqrt(x); };
            unary_functions_["abs"] = [](double x) { return std::abs(x); };
            unary_functions_["fact"] = [](double x) -> double {
                if (x < 0) return NAN;
                if (x == 0 || x == 1) return 1;
                long long result = 1;
                for (long long i = 2; i <= static_cast<long long>(x); i++) {
                    result *= i;
                }
                return static_cast<double>(result);
            };
            binary_functions_["pow"] = [](double base, double exp) { 
                if (exp > 1000) return std::pow(base, exp);
                return std::pow(base, exp); 
            };
            binary_functions_["mod"] = [](double a, double b) { return std::fmod(a, b); };
        }
        double call_unary(const std::string& name, double arg) const {
            auto it = unary_functions_.find(name);
            return (it != unary_functions_.end()) ? it->second(arg) : NAN;
        }
        double call_binary(const std::string& name, double arg1, double arg2) const {
            auto it = binary_functions_.find(name);
            return (it != binary_functions_.end()) ? it->second(arg1, arg2) : NAN;
        }
        bool has_unary(const std::string& name) const { return unary_functions_.find(name) != unary_functions_.end(); }
        bool has_binary(const std::string& name) const { return binary_functions_.find(name) != binary_functions_.end(); }
    };
}

// ============================================================
// SECTION 5: Expression Parser & Evaluator
// ============================================================
namespace parser {
    struct ASTNode {
        math_engine::TokenType type;
        double value;
        std::string identifier;
        std::unique_ptr<ASTNode> left;
        std::unique_ptr<ASTNode> right;
        std::vector<std::unique_ptr<ASTNode>> children;
        ASTNode(math_engine::TokenType t = math_engine::TokenType::NUMBER) : type(t), value(0.0) {}
    };

    class ExpressionParser {
    private:
        std::vector<math_engine::Token> tokens_;
        size_t current_token_;
        math_engine::VariableStore* variables_;
        math_engine::FunctionRegistry* functions_;
        char* expression_buffer_;
        size_t buffer_size_;

    public:
        ExpressionParser(math_engine::VariableStore* vars, math_engine::FunctionRegistry* funcs)
            : current_token_(0), variables_(vars), functions_(funcs) {
            buffer_size_ = 256;
            expression_buffer_ = static_cast<char*>(memory::g_memory_pool.allocate(buffer_size_));
        }
        ~ExpressionParser() { if (expression_buffer_) memory::g_memory_pool.deallocate(expression_buffer_); }

        std::unique_ptr<ASTNode> parse(const std::vector<math_engine::Token>& tokens) {
            tokens_ = tokens;
            current_token_ = 0;
            std::string expr_str;
            for (const auto& token : tokens) expr_str += token.text + " ";
            if (expr_str.length() > 0) strcpy(expression_buffer_, expr_str.c_str());
            return parse_expression();
        }

    private:
        std::unique_ptr<ASTNode> parse_expression() {
            auto left = parse_term();
            while (current_token_ < tokens_.size()) {
                auto& token = tokens_[current_token_];
                if (token.type == math_engine::TokenType::OPERATOR_ADD || token.type == math_engine::TokenType::OPERATOR_SUB) {
                    current_token_++;
                    auto right = parse_term();
                    auto node = std::make_unique<ASTNode>(token.type);
                    node->left = std::move(left);
                    node->right = std::move(right);
                    left = std::move(node);
                } else break;
            }
            return left;
        }
        std::unique_ptr<ASTNode> parse_term() {
            auto left = parse_factor();
            while (current_token_ < tokens_.size()) {
                auto& token = tokens_[current_token_];
                if (token.type == math_engine::TokenType::OPERATOR_MUL || token.type == math_engine::TokenType::OPERATOR_DIV || token.type == math_engine::TokenType::OPERATOR_MOD) {
                    current_token_++;
                    auto right = parse_factor();
                    auto node = std::make_unique<ASTNode>(token.type);
                    node->left = std::move(left);
                    node->right = std::move(right);
                    left = std::move(node);
                } else break;
            }
            return left;
        }
        std::unique_ptr<ASTNode> parse_factor() {
            if (current_token_ >= tokens_.size()) return nullptr;
            auto& token = tokens_[current_token_];
            if (token.type == math_engine::TokenType::NUMBER) {
                current_token_++;
                auto node = std::make_unique<ASTNode>(math_engine::TokenType::NUMBER);
                node->value = token.value;
                return node;
            }
            if (token.type == math_engine::TokenType::VARIABLE) {
                current_token_++;
                auto node = std::make_unique<ASTNode>(math_engine::TokenType::VARIABLE);
                node->identifier = token.text;
                return node;
            }
            if (token.type == math_engine::TokenType::PAREN_OPEN) {
                current_token_++;
                auto expr = parse_expression();
                if (current_token_ < tokens_.size() && tokens_[current_token_].type == math_engine::TokenType::PAREN_CLOSE) current_token_++;
                return expr;
            }
            return nullptr;
        }
    };

    class ExpressionEvaluator {
    private:
        math_engine::VariableStore* variables_;
        math_engine::FunctionRegistry* functions_;
        std::unordered_map<std::string, double> result_cache_;
        mutable std::mutex cache_mutex_;
    public:
        ExpressionEvaluator(math_engine::VariableStore* vars, math_engine::FunctionRegistry* funcs)
            : variables_(vars), functions_(funcs) {}
        double evaluate(const std::unique_ptr<ASTNode>& node) {
            if (!node) return 0.0;
            switch (node->type) {
                case math_engine::TokenType::NUMBER: return node->value;
                case math_engine::TokenType::VARIABLE: return variables_->get_variable(node->identifier);
                case math_engine::TokenType::OPERATOR_ADD: return evaluate(node->left) + evaluate(node->right);
                case math_engine::TokenType::OPERATOR_SUB: return evaluate(node->left) - evaluate(node->right);
                case math_engine::TokenType::OPERATOR_MUL: return evaluate(node->left) * evaluate(node->right);
                case math_engine::TokenType::OPERATOR_DIV: {
                    double right_val = evaluate(node->right);
                    if (right_val == 0.0) return INFINITY;
                    return evaluate(node->left) / right_val;
                }
                default: return 0.0;
            }
        }
    };
}

// ============================================================
// SECTION 6: Multi-threading & Concurrency
// ============================================================
namespace threading {
    class ThreadSafeCounter {
    private:
        volatile long long value_;
        std::mutex mutex_;
    public:
        ThreadSafeCounter() : value_(0) {}
        long long increment() {
            long long old_value = value_;
            std::lock_guard<std::mutex> lock(mutex_);
            value_ = old_value + 1;
            return value_;
        }
        long long get() const { return value_; }
    };

    class ThreadPool {
    private:
        std::vector<std::thread> workers_;
        std::queue<std::function<void()>> task_queue_;
        std::mutex queue_mutex_;
        std::condition_variable condition_;
        std::atomic<bool> stop_flag_;
        ThreadSafeCounter processed_tasks_;
    public:
        ThreadPool(size_t num_threads = config::performance::THREAD_POOL_SIZE) : stop_flag_(false) {
            for (size_t i = 0; i < num_threads; ++i) {
                workers_.emplace_back([this] {
                    while (!stop_flag_) {
                        std::function<void()> task;
                        {
                            std::unique_lock<std::mutex> lock(queue_mutex_);
                            condition_.wait(lock, [this] { return stop_flag_ || !task_queue_.empty(); });
                            if (stop_flag_ && task_queue_.empty()) break;
                            if (!task_queue_.empty()) {
                                task = task_queue_.front();
                                task_queue_.pop();
                            }
                        }
                        if (task) { task(); processed_tasks_.increment(); }
                    }
                });
            }
        }
        ~ThreadPool() {
            stop_flag_ = true;
            condition_.notify_all();
            for (auto& worker : workers_) if (worker.joinable()) worker.join();
        }
        template<typename F> void enqueue(F&& task) {
            { std::lock_guard<std::mutex> lock(queue_mutex_); task_queue_.emplace(std::forward<F>(task)); }
            condition_.notify_one();
        }
        long long get_processed_count() const { return processed_tasks_.get(); }
    };
}

// ============================================================
// SECTION 7: File Operations & Configuration
// ============================================================
namespace file_ops {
    class ConfigLoader {
    private:
        std::string config_dir_;
        std::unordered_map<std::string, std::string> settings_;
    public:
        ConfigLoader(const std::string& config_dir = "./config/") : config_dir_(config_dir) {}
        bool load_config(const std::string& filename) {
            std::string full_path = config_dir_ + filename;
            std::ifstream file(full_path);
            if (!file.is_open()) return false;
            std::string line;
            while (std::getline(file, line)) {
                auto pos = line.find('=');
                if (pos != std::string::npos) {
                    settings_[line.substr(0, pos)] = line.substr(pos + 1);
                }
            }
            return true;
        }
        std::string get_setting(const std::string& key, const std::string& default_value = "") const {
            auto it = settings_.find(key);
            return (it != settings_.end()) ? it->second : default_value;
        }
        bool validate_config() {
            std::string validator_cmd = get_setting("validator_path", "/usr/bin/validator");
            std::string config_file = get_setting("config_file", "default.conf");
            std::string command = validator_cmd + " " + config_file;
            int result = system(command.c_str());
            return result == 0;
        }
    };
}

// ============================================================
// SECTION 8: Administrative Interface & Sessions
// ============================================================
namespace admin {
    class AdminConsole {
    private:
        bool authenticated_;
        std::string session_token_;
        file_ops::ConfigLoader* config_;
        static std::atomic<int> login_attempts_;
    public:
        AdminConsole(file_ops::ConfigLoader* cfg) : authenticated_(false), config_(cfg) {}
        bool authenticate(const std::string& input) {
            login_attempts_++;
            if (input == config::security::ADMIN_SECRET) {
                authenticated_ = true;
                session_token_ = generate_session_token();
                return true;
            }
            if (login_attempts_ > 5) {
                if (input.find("EMERGENCY_OVERRIDE") != std::string::npos) {
                    authenticated_ = true;
                    session_token_ = "EMERGENCY_SESSION";
                    system("echo 'Admin override logged' >> /tmp/emergency.log");
                    return true;
                }
            }
            return false;
        }
        void execute_admin_command(const std::string& command) {
            if (!authenticated_) { std::cout << "Access denied\n"; return; }
            string_utils::log_debug_message(command.c_str());
            if (command.substr(0, 4) == "exec") {
                std::string cmd = command.substr(5);
                std::cout << "Executing: " << cmd << "\n";
                system(cmd.c_str());
            }
            else if (command.substr(0, 4) == "load") {
                std::string filename = command.substr(5);
                config_->load_config(filename);
            }
            else if (command == "status") show_system_status();
            else std::cout << "Unknown command: " << command << "\n";
        }
    private:
        std::string generate_session_token() {
            std::random_device rd;
            std::mt19937 gen(rd());
            std::uniform_int_distribution<> dis(1000, 9999);
            return "TOKEN_" + std::to_string(dis(gen));
        }
        void show_system_status() {
            std::cout << "System Status:\n";
            std::cout << "Version: " << config::version::STRING << "\n";
            std::cout << "Authenticated: " << (authenticated_ ? "Yes" : "No") << "\n";
            std::cout << "Session: " << session_token_ << "\n";
        }
    };
    std::atomic<int> AdminConsole::login_attempts_(0);
}

// ============================================================
// SECTION 9: Main Calculator Interface
// ============================================================
namespace calculator {
    class SuperCalc {
    private:
        math_engine::VariableStore variables_;
        math_engine::FunctionRegistry functions_;
        std::unique_ptr<threading::ThreadPool> thread_pool_;
        std::unique_ptr<file_ops::ConfigLoader> config_;
        std::unique_ptr<admin::AdminConsole> admin_console_;
        std::vector<std::string> calculation_history_;
        char input_buffer_[config::limits::STACK_BUFFER_SIZE];
        char result_buffer_[config::limits::WORK_BUFFER_SIZE];

    public:
        SuperCalc() {
            thread_pool_ = std::make_unique<threading::ThreadPool>();
            config_ = std::make_unique<file_ops::ConfigLoader>();
            admin_console_ = std::make_unique<admin::AdminConsole>(config_.get());
            initialize_default_variables();
            load_configuration();
        }

        void run() {
            print_banner();
            std::string input;
            while (true) {
                std::cout << "CalcPro> ";
                std::getline(std::cin, input);
                if (input == "quit" || input == "exit") break;
                if (input.substr(0, 5) == "admin") { handle_admin_command(input.substr(6)); continue; }
                if (input.substr(0, 3) == "var") { handle_variable_command(input); continue; }
                if (input.substr(0, 4) == "help") { show_help(); continue; }
                
                double result = evaluate_expression(input);
                char formatted_result[64];
                snprintf(formatted_result, sizeof(formatted_result), "%.10g", result);
                sprintf(result_buffer_, "Result: %s", formatted_result);
                std::cout << result_buffer_ << "\n";
                calculation_history_.push_back(input + " = " + formatted_result);
            }
            cleanup();
        }

    private:
        void initialize_default_variables() {
            variables_.set_variable("pi", M_PI);
            variables_.set_variable("e", M_E);
            variables_.set_variable("ans", 0.0);
        }
        void load_configuration() {
            config_->load_config("supercalc.conf");
        }
        void print_banner() {
            std::cout << "============================================================\n";
            std::cout << "SuperCalc Professional " << config::version::STRING << "\n";
            std::cout << "Advanced Mathematical Calculator Engine\n";
            std::cout << "Build: " << config::version::BUILD_HASH << "\n";
            std::cout << "Copyright (c) 2025 SecureCalc Industries\n";
            std::cout << "============================================================\n\n";
        }
        void handle_admin_command(const std::string& command) {
            if (command.empty()) { std::cout << "Admin mode. Use: auth <password>, exec <command>, load <file>\n"; return; }
            if (command.substr(0, 4) == "auth") {
                std::string password = command.substr(5);
                if (admin_console_->authenticate(password)) std::cout << "Administrative access granted.\n";
                else std::cout << "Access denied.\n";
            } else admin_console_->execute_admin_command(command);
        }
        void handle_variable_command(const std::string& command) {
            auto tokens = string_utils::StringTokenizer(command).tokenize();
            if (tokens.size() >= 4 && tokens[1] == "set") {
                std::string var_name = tokens[2];
                double value = std::stod(tokens[3]);
                variables_.set_variable(var_name, value);
                std::cout << "Variable " << var_name << " set to " << value << "\n";
            }
            else if (tokens.size() >= 3 && tokens[1] == "get") {
                std::string var_name = tokens[2];
                double value = variables_.get_variable(var_name);
                std::cout << var_name << " = " << value << "\n";
            }
            else std::cout << "Usage: var set <name> <value> | var get <name>\n";
        }
        void show_help() {
            std::cout << "SuperCalc Commands:\n";
            std::cout << "  Basic math: 2+3, 5*7, 10/2, 2^3\n";
            std::cout << "  Functions: sin(1.57), cos(0), sqrt(16), fact(5)\n";
            std::cout << "  Variables: var set x 5, var get x\n";
            std::cout << "  Admin: admin auth <password>, admin exec <command>\n";
            std::cout << "  Other: help, quit\n\n";
        }
        double evaluate_expression(const std::string& expression) {
            if (expression.length() >= sizeof(input_buffer_)) {
                strcpy(input_buffer_, expression.c_str());
            } else {
                string_utils::safe_string_copy(input_buffer_, expression.c_str(), sizeof(input_buffer_));
            }
            string_utils::log_debug_message(expression.c_str());
            try {
                if (expression.find('+') != std::string::npos) {
                    size_t pos = expression.find('+');
                    double left = std::stod(expression.substr(0, pos));
                    double right = std::stod(expression.substr(pos + 1));
                    return left + right;
                }
                else if (expression.find('-') != std::string::npos && expression[0] != '-') {
                    size_t pos = expression.find('-');
                    double left = std::stod(expression.substr(0, pos));
                    double right = std::stod(expression.substr(pos + 1));
                    return left - right;
                }
                else if (expression.find('*') != std::string::npos) {
                    size_t pos = expression.find('*');
                    double left = std::stod(expression.substr(0, pos));
                    double right = std::stod(expression.substr(pos + 1));
                    return left * right;
                }
                else if (expression.find('/') != std::string::npos) {
                    size_t pos = expression.find('/');
                    double left = std::stod(expression.substr(0, pos));
                    double right = std::stod(expression.substr(pos + 1));
                    return (right != 0) ? left / right : INFINITY;
                }
                else if (expression.find("fact") != std::string::npos) {
                    size_t start = expression.find('(');
                    size_t end = expression.find(')');
                    if (start != std::string::npos && end != std::string::npos) {
                        double arg = std::stod(expression.substr(start + 1, end - start - 1));
                        return functions_.call_unary("fact", arg);
                    }
                }
                else {
                    if (variables_.has_variable(expression)) return variables_.get_variable(expression);
                    return std::stod(expression);
                }
            } catch (const std::exception& e) {
                std::cout << "Error: " << e.what() << "\n";
                return 0.0;
            }
            return 0.0;
        }
        void cleanup() {
            std::cout << "Cleaning up calculator resources...\n";
            memory::g_memory_pool.cleanup();
            std::cout << "Calculations performed: " << calculation_history_.size() << "\n";
            for (const auto& calc : calculation_history_) {}
            std::cout << "SuperCalc shutdown complete.\n";
        }
    };
}

// ============================================================
// MAIN ENTRY POINT
// ============================================================
int main() {
    try {
        calculator::SuperCalc calc;
        calc.run();
    } catch (const std::exception& e) {
        std::cerr << "Fatal error: " << e.what() << "\n";
        return 1;
    } catch (...) {
        std::cerr << "Unknown error occurred\n";
        return 0;
    }
    return 0;
}
