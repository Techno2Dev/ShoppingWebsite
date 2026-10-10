import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../environments/environment';

export interface Product {
  id: number;
  name: string;
  description: string;
  price: number;
  image: string;
  imageUrl?: string | null;
  category: string;
  stock: number;
}

export interface CartLine {
  product: Product;
  quantity: number;
}

export interface OrderItem {
  id: number;
  orderId: number;
  productId: number;
  productName: string;
  quantity: number;
  unitPrice: number;
}

export interface StoreOrder {
  id: number;
  customerName: string;
  email: string;
  address: string;
  paymentStatus: string;
  createdAt: string;
  paymentReference?: string | null;
  paymentReportedAt?: string | null;
  total: number;
  items: OrderItem[];
}

export interface CreatedStoreOrder extends StoreOrder {
  accessToken: string;
}

export interface AdminSummary {
  totalOrders: number;
  revenue: number;
  pendingPayments: number;
  productCount: number;
}

export interface AdminSession {
  userName: string;
  accessToken: string;
}

@Injectable({ providedIn: 'root' })
export class StoreApiService {
  private readonly apiUrl = environment.apiUrl;
  private adminToken = '';

  constructor(private http: HttpClient) {
    try { this.adminToken = sessionStorage.getItem('sapana-admin-session') || ''; }
    catch (error) { /* Session storage may be disabled. */ }
  }

  private adminHeaders(): HttpHeaders {
    return new HttpHeaders({ Authorization: 'Bearer ' + this.adminToken });
  }

  private saveAdminToken(token: string): void {
    this.adminToken = token;
    try { sessionStorage.setItem('sapana-admin-session', token); }
    catch (error) { /* Keep the session in memory when storage is unavailable. */ }
  }

  clearAdminToken(): void {
    this.adminToken = '';
    try { sessionStorage.removeItem('sapana-admin-session'); }
    catch (error) { /* Session storage may be disabled. */ }
  }

  getProducts(): Observable<Product[]> {
    const apiOrigin = new URL(this.apiUrl).origin;
    return this.http.get<Product[]>(this.apiUrl + '/products').pipe(map(products =>
      products.map(product => ({
        ...product,
        imageUrl: product.imageUrl ? new URL(product.imageUrl, apiOrigin).toString() : null
      }))
    ));
  }

  createProduct(product: Omit<Product, 'id' | 'imageUrl'>, imageFile?: File): Observable<Product> {
    const formData = new FormData();
    formData.append('name', product.name);
    formData.append('description', product.description);
    formData.append('price', String(product.price));
    formData.append('image', product.image);
    formData.append('category', product.category);
    formData.append('stock', String(product.stock));
    if (imageFile) {
      formData.append('imageFile', imageFile, imageFile.name);
    }
    return this.http.post<Product>(this.apiUrl + '/admin/products', formData, { headers: this.adminHeaders() });
  }

  deleteProduct(productId: number): Observable<void> {
    return this.http.delete<void>(this.apiUrl + '/admin/products/' + productId, { headers: this.adminHeaders() });
  }

  getOrders(): Observable<StoreOrder[]> {
    return this.http.get<StoreOrder[]>(this.apiUrl + '/orders', { headers: this.adminHeaders() });
  }

  getSummary(): Observable<AdminSummary> {
    return this.http.get<AdminSummary>(this.apiUrl + '/admin/summary', { headers: this.adminHeaders() });
  }

  adminLogin(userName: string, password: string): Observable<AdminSession> {
    return this.http.post<AdminSession>(
      this.apiUrl + '/admin/auth/login',
      { userName: userName, password: password }
    ).pipe(map(session => {
      this.saveAdminToken(session.accessToken);
      return session;
    }));
  }

  getAdminSession(): Observable<{ userName: string }> {
    return this.http.get<{ userName: string }>(this.apiUrl + '/admin/auth/session', { headers: this.adminHeaders() });
  }

  updateAdminCredentials(credentials: {
    currentPassword: string;
    newUserName: string;
    newPassword: string;
  }): Observable<AdminSession> {
    return this.http.post<AdminSession>(
      this.apiUrl + '/admin/auth/credentials',
      credentials,
      { headers: this.adminHeaders() }
    ).pipe(map(session => {
      this.saveAdminToken(session.accessToken);
      return session;
    }));
  }

  adminLogout(): Observable<void> {
    return this.http.post<void>(this.apiUrl + '/admin/auth/logout', {}, { headers: this.adminHeaders() })
      .pipe(map(result => {
        this.clearAdminToken();
        return result;
      }));
  }

  createOrder(order: {
    customerName: string;
    email: string;
    address: string;
    items: { productId: number; quantity: number }[];
  }): Observable<CreatedStoreOrder> {
    return this.http.post<CreatedStoreOrder>(this.apiUrl + '/orders', order);
  }

  getCustomerOrderStatus(orderId: number, accessToken: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders/' + orderId + '/payment-status', {
      accessToken: accessToken
    });
  }

  reportUpiPayment(orderId: number, accessToken: string, transactionReference: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders/' + orderId + '/payment-reported', {
      accessToken: accessToken,
      transactionReference: transactionReference.trim() || null
    });
  }

  updatePaymentStatus(orderId: number, paymentStatus: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(
      this.apiUrl + '/admin/orders/' + orderId + '/payment-status',
      { paymentStatus: paymentStatus },
      { headers: this.adminHeaders() }
    );
  }
}
